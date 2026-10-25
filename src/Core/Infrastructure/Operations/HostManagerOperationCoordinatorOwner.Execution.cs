using System.Buffers.Binary;
using System.Security.Cryptography;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

public sealed partial class HostManagerOperationCoordinatorOwner
{
    private async Task RunPlannerLoopAsync(
        CancellationToken ownerStopping,
        TaskCompletionSource firstPlannerCycle)
    {
        try
        {
            ulong? nextWake = null;
            while (!ownerStopping.IsCancellationRequested)
            {
                if (nextWake is null)
                {
                    await plannerWake.WaitAsync(ownerStopping).ConfigureAwait(false);
                }
                else
                {
                    var now =
                        NativeOperationCoordinatorWorkspace.MonotonicMilliseconds();
                    if (nextWake.Value > now)
                    {
                        var delay = TimeSpan.FromMilliseconds(
                            checked((double)(nextWake.Value - now)));
                        _ = await plannerWake.WaitAsync(delay, ownerStopping)
                            .ConfigureAwait(false);
                    }
                }
                await ApplyPendingQuiescentPlanAsync(ownerStopping)
                    .ConfigureAwait(false);
                nextWake = await PlanOnceAsync(ownerStopping)
                    .ConfigureAwait(false);
                firstPlannerCycle.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (ownerStopping.IsCancellationRequested)
        {
            firstPlannerCycle.TrySetCanceled(ownerStopping);
        }
        catch (Exception error)
        {
            firstPlannerCycle.TrySetException(error);
            await LatchWorkerFaultAsync(error, "planner-worker").ConfigureAwait(false);
        }
    }

    private async Task<ulong?> PlanOnceAsync(CancellationToken ownerStopping)
    {
        List<HostManagerOperationActionTicket> tickets = [];
        ulong? nextWake = null;
        await mutationGate.WaitAsync(ownerStopping).ConfigureAwait(false);
        try
        {
            if (!TryRequireMutableLocked())
            {
                return null;
            }
            var batch = workspace!.PlanAndReadActionsLocked(
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                NativeOperationCoordinatorWorkspace.MonotonicMilliseconds());
            var candidatePayloads = payloads.Clone();
            var candidateReceipts = receipts.Clone();
            var operations = workspace.ReadCommittedProjectionLocked()
                .ToDictionary(static operation => operation.OperationId);
            var createdAt = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var action in batch.Actions)
            {
                if (!operations.TryGetValue(action.OperationId, out var operation))
                {
                    throw new InvalidDataException(
                        "A planned operation action has no native operation record.");
                }
                var kindEntry = payloads.Require(operation.KindHandle);
                var requestEntry = payloads.Require(operation.RequestHandle);
                var kind =
                    HostManagerOperationRequestCodec.DecodeText(kindEntry.Payload);
                _ = effectRouter.Require(kind);
                var effectKind = EffectKindCode(kind);
                var receiptId = CreateReceiptId(
                    workspace.SessionInstanceId,
                    in action,
                    effectKind,
                    effectPhase: 1);
                var expectedBefore = candidatePayloads.Add(
                    HostManagerOperationRequestSchemas.EffectExpectedBefore,
                    HostManagerOperationRequestSchemas.Version,
                    HostManagerOperationPayloadRole.EffectExpectedBefore,
                    workspace.SessionInstanceId,
                    action.OperationId,
                    action.AttemptToken,
                    HostManagerOperationRequestCodec.EncodeEffectExpectedBefore(
                        requestEntry.Handle));
                candidateReceipts.Upsert(new HostManagerOperationEffectReceipt(
                    HostManagerOperationEffectReceiptState.Prepared,
                    HostManagerOperationEffectReceiptOutcome.None,
                    receiptId,
                    workspace.SessionInstanceId,
                    action.OperationId,
                    action.AttemptToken,
                    action.ActionId,
                    action.PlanEpoch,
                    action.ConfigurationGeneration,
                    1,
                    (NativeOperationActionKind)action.Kind,
                    effectKind,
                    1,
                    createdAt,
                    createdAt,
                    expectedBefore.Handle,
                    default,
                    default,
                    default));
                tickets.Add(new HostManagerOperationActionTicket(
                    action,
                    kind,
                    requestEntry,
                    receiptId));
            }
            CommitCurrentLocked(
                "plan-admission-publication",
                candidatePayloads,
                candidateReceipts);
            if ((batch.Plan.Flags
                & (ulong)NativeOperationSnapshotFlags.NextWakeValid) != 0)
            {
                nextWake = batch.Plan.NextWakeMonotonicMilliseconds;
            }
        }
        finally
        {
            mutationGate.Release();
        }

        var writer = actionChannel?.Writer
            ?? throw new InvalidOperationException(
                "The operation action admission channel is absent.");
        foreach (var ticket in tickets)
        {
            await writer.WriteAsync(ticket, ownerStopping).ConfigureAwait(false);
        }
        return nextWake;
    }

    private async Task RunActionWorkerAsync(CancellationToken ownerStopping)
    {
        try
        {
            var reader = actionChannel?.Reader
                ?? throw new InvalidOperationException(
                    "The operation action admission channel is absent.");
            await foreach (var ticket in reader.ReadAllAsync(ownerStopping)
                               .ConfigureAwait(false))
            {
                await ExecuteTicketAsync(ticket, ownerStopping)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ownerStopping.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            await LatchWorkerFaultAsync(error, "action-worker").ConfigureAwait(false);
        }
    }

    private async Task ExecuteTicketAsync(
        HostManagerOperationActionTicket ticket,
        CancellationToken ownerStopping)
    {
        var now = NativeOperationCoordinatorWorkspace.MonotonicMilliseconds();
        if (IsActionExpired(now, ticket.Action.DeadlineMonotonicMilliseconds))
        {
            await SettleExpiredTicketAsync(ticket, ownerStopping)
                .ConfigureAwait(false);
            return;
        }
        switch ((NativeOperationActionKind)ticket.Action.Kind)
        {
            case NativeOperationActionKind.Start:
                await ExecuteStartTicketAsync(ticket, ownerStopping)
                    .ConfigureAwait(false);
                return;
            case NativeOperationActionKind.Cancel:
                await ExecuteCancelTicketAsync(ticket, ownerStopping)
                    .ConfigureAwait(false);
                return;
            case NativeOperationActionKind.Recover:
                await ExecuteRecoveryTicketAsync(ticket, ownerStopping)
                    .ConfigureAwait(false);
                return;
            default:
                throw new InvalidDataException(
                    "The admitted operation action kind is unknown.");
        }
    }

    internal static bool IsActionExpired(
        ulong nowMonotonicMilliseconds,
        ulong deadlineMonotonicMilliseconds)
        => nowMonotonicMilliseconds >= deadlineMonotonicMilliseconds;

    private async Task ExecuteStartTicketAsync(
        HostManagerOperationActionTicket ticket,
        CancellationToken ownerStopping)
    {
        var control = await MarkActionStartedAsync(ticket, ownerStopping)
            .ConfigureAwait(false);
        if (control is null)
        {
            return;
        }
        HostManagerOperationEffectCompletion completion;
        try
        {
            completion = await effectRouter.Require(ticket.Kind)
                .ExecuteStartAsync(ticket, this, control.Token)
                .ConfigureAwait(false);
            completion = NormalizeStartedCompletion(completion);
        }
        catch (Exception error)
        {
            completion = new(
                HostManagerOperationEffectOutcome.Uncertain,
                HostManagerOperationFeedback.Failure(error));
        }
        finally
        {
            attemptArbiter.Complete(
                ticket.Action.OperationId,
                ticket.Action.AttemptToken);
        }
        await SettleCompletionAsync(ticket, completion, ownerStopping)
            .ConfigureAwait(false);
    }

    internal static HostManagerOperationEffectCompletion
        NormalizeStartedCompletion(
            HostManagerOperationEffectCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return completion.Outcome == HostManagerOperationEffectOutcome.Succeeded
            ? completion
            : new HostManagerOperationEffectCompletion(
                HostManagerOperationEffectOutcome.Uncertain,
                completion.Message);
    }

    private async Task ExecuteCancelTicketAsync(
        HostManagerOperationActionTicket ticket,
        CancellationToken ownerStopping)
    {
        var completion = attemptArbiter.RequestCancel(
            ticket.Action.OperationId,
            ticket.Action.AttemptToken);
        if (completion is null)
        {
            await ApplyActionFeedbackAsync(
                    ticket,
                    NativeOperationActionFeedbackOutcome.CancelRetryableFailure,
                    HostManagerOperationEffectReceiptOutcome.EffectNotObserved,
                    HostManagerOperationFeedback.Reason(ResourceManager.App.Domain.Messages.BackendMessageCodes.Operation.NoExecutionEvidence),
                    ownerStopping)
                .ConfigureAwait(false);
            return;
        }
        await completion.WaitAsync(ownerStopping).ConfigureAwait(false);
        await ApplyActionFeedbackAsync(
                ticket,
                NativeOperationActionFeedbackOutcome.CancelCompleted,
                HostManagerOperationEffectReceiptOutcome.Canceled,
                string.Empty,
                ownerStopping)
            .ConfigureAwait(false);
    }

    private async Task ExecuteRecoveryTicketAsync(
        HostManagerOperationActionTicket ticket,
        CancellationToken ownerStopping)
    {
        HostManagerOperationEffectReceipt evidence;
        HostManagerOperationEffectReceipt? originalEvidence = null;
        var missingPriorEvidence = false;
        await mutationGate.WaitAsync(ownerStopping).ConfigureAwait(false);
        try
        {
            if (!TryRequireMutableLocked())
            {
                return;
            }
            var operation = workspace!.ReadCommittedProjectionLocked().Single(record => record.OperationId == ticket.Action.OperationId);
            // A recovery action has its own authorization token. Readback must bind the original effect attempt, never that new action token.
            var prior = receipts.FindLatest(
                    ticket.Action.OperationId,
                    operation.AttemptToken,
                    ticket.ReceiptId);
            evidence = prior ?? receipts.Require(ticket.ReceiptId);
            originalEvidence = prior;
            RequireExactReadbackBinding(ticket, evidence, prior is null ? ticket.Action.AttemptToken : operation.AttemptToken);
            missingPriorEvidence = prior is null && !operation.AttemptToken.IsZero;
            if (missingPriorEvidence)
                logger.LogWarning("Operation {OperationId} has no receipt for original effect attempt {AttemptToken}; recovery is uncertain and will not re-execute the effect.",
                    FormatHandle(operation.OperationId), FormatHandle(operation.AttemptToken));
        }
        finally
        {
            mutationGate.Release();
        }

        HostManagerOperationEffectCompletion readback;
        try
        {
            readback = missingPriorEvidence
                ? new(HostManagerOperationEffectOutcome.Uncertain,
                    HostManagerOperationFeedback.Reason(ResourceManager.App.Domain.Messages.BackendMessageCodes.Operation.OutcomeUncertain))
                : await effectRouter.Require(ticket.Kind)
                    .ReadbackAsync(ticket, evidence, ownerStopping)
                    .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            readback = new(
                HostManagerOperationEffectOutcome.Uncertain,
                HostManagerOperationFeedback.Failure(error));
        }
        var (feedback, outcome) = readback.Outcome switch
        {
            HostManagerOperationEffectOutcome.RetryableFailure => (
                NativeOperationActionFeedbackOutcome.RecoveredQueued,
                HostManagerOperationEffectReceiptOutcome.EffectNotObserved),
            HostManagerOperationEffectOutcome.Succeeded => (
                NativeOperationActionFeedbackOutcome.RecoveredSucceeded,
                HostManagerOperationEffectReceiptOutcome.Succeeded),
            HostManagerOperationEffectOutcome.TerminalFailure => (
                NativeOperationActionFeedbackOutcome.RecoveredFailed,
                HostManagerOperationEffectReceiptOutcome.TerminalFailure),
            HostManagerOperationEffectOutcome.Canceled => (
                NativeOperationActionFeedbackOutcome.RecoveredCanceled,
                HostManagerOperationEffectReceiptOutcome.Canceled),
            _ => (
                NativeOperationActionFeedbackOutcome.RecoveredUncertain,
                HostManagerOperationEffectReceiptOutcome.Uncertain)
        };
        await ApplyActionFeedbackAsync(
                ticket,
                feedback,
                outcome,
                readback.Message,
                ownerStopping,
                originalEvidence)
            .ConfigureAwait(false);
    }

    private async Task<HostManagerOperationAttemptArbiter.AttemptControl?>
        MarkActionStartedAsync(
            HostManagerOperationActionTicket ticket,
            CancellationToken ownerStopping)
    {
        await mutationGate.WaitAsync(ownerStopping).ConfigureAwait(false);
        try
        {
            if (!TryRequireMutableLocked())
            {
                return null;
            }
            var candidateReceipts = receipts.Clone();
            var current = candidateReceipts.Require(ticket.ReceiptId);
            var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            candidateReceipts.Upsert(current with
            {
                State = HostManagerOperationEffectReceiptState.Started,
                ReceiptRevision = current.ReceiptRevision + 1,
                UpdatedUtcMilliseconds = now
            });
            var input = new NativeOperationActionFeedbackInput
            {
                ActionId = ticket.Action.ActionId,
                PlanEpoch = ticket.Action.PlanEpoch,
                OperationId = ticket.Action.OperationId,
                AttemptToken = ticket.Action.AttemptToken,
                ActionKind = ticket.Action.Kind,
                Outcome = (uint)NativeOperationActionFeedbackOutcome.Started,
                ObservedUtcMilliseconds = now,
                ObservedMonotonicMilliseconds =
                    NativeOperationCoordinatorWorkspace.MonotonicMilliseconds(),
                ValidMask = 0,
                Flags = 0
            };
            if (!workspace!.ApplyActionFeedbackLocked(ref input))
            {
                return null;
            }
            CommitCurrentLocked(
                "effect-started-publication",
                payloads.Clone(),
                candidateReceipts);
            return attemptArbiter.Register(
                ticket.Action.OperationId,
                ticket.Action.AttemptToken);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private async Task SettleCompletionAsync(
        HostManagerOperationActionTicket ticket,
        HostManagerOperationEffectCompletion completion,
        CancellationToken ownerStopping)
    {
        await mutationGate.WaitAsync(ownerStopping).ConfigureAwait(false);
        try
        {
            if (!TryRequireMutableLocked())
            {
                return;
            }
            var candidatePayloads = payloads.Clone();
            var candidateReceipts = receipts.Clone();
            var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var input = new NativeOperationCompletionInput
            {
                OperationId = ticket.Action.OperationId,
                AttemptToken = ticket.Action.AttemptToken,
                Outcome = (uint)MapCompletionOutcome(completion.Outcome),
                ObservedUtcMilliseconds = now,
                ObservedMonotonicMilliseconds =
                    NativeOperationCoordinatorWorkspace.MonotonicMilliseconds(),
                Flags = 0
            };
            var payloadRole = completion.Outcome == HostManagerOperationEffectOutcome.Succeeded
                ? HostManagerOperationPayloadRole.Result
                : HostManagerOperationPayloadRole.Error;
            var schema = completion.Outcome == HostManagerOperationEffectOutcome.Succeeded
                ? HostManagerOperationRequestSchemas.OperationResult
                : HostManagerOperationRequestSchemas.OperationError;
            var isResult = payloadRole == HostManagerOperationPayloadRole.Result;
            var hasMessage = !string.IsNullOrWhiteSpace(completion.Message);
            var evidenceText = hasMessage
                ? completion.Message
                : OutcomeEvidenceToken(completion.Outcome);
            // 失败的错误载荷同时是收据证据，一定要写；成功的结果载荷只是给用户看的文本，
            // 没话说就不写 —— 载荷目录不允许存在没人引用的条目。
            var message = !isResult || hasMessage
                ? AddTextLocked(candidatePayloads, schema, payloadRole, ticket, evidenceText)
                : null;
            if (hasMessage && message is not null)
            {
                if (isResult)
                {
                    input.ResultHandle = message.Handle;
                    input.ValidMask = (ulong)NativeOperationResultValidity.Result;
                }
                else
                {
                    input.ErrorHandle = message.Handle;
                    input.ValidMask = (ulong)NativeOperationResultValidity.Error;
                }
            }
            var observation = isResult
                ? AddTextLocked(
                    candidatePayloads,
                    HostManagerOperationRequestSchemas.EffectObservation,
                    HostManagerOperationPayloadRole.EffectObservation,
                    ticket,
                    evidenceText)
                : null;
            var accepted = workspace!.CompleteLocked(ref input);
            var current = candidateReceipts.Require(ticket.ReceiptId);
            candidateReceipts.Upsert(current with
            {
                State = completion.Outcome == HostManagerOperationEffectOutcome.Uncertain
                    ? HostManagerOperationEffectReceiptState.Uncertain
                    : HostManagerOperationEffectReceiptState.Settled,
                Outcome = MapReceiptOutcome(completion.Outcome),
                ReceiptRevision = current.ReceiptRevision + 1,
                UpdatedUtcMilliseconds = now,
                ObservationHandle = observation is not null
                    ? observation.Handle
                    : current.ObservationHandle,
                ErrorHandle = !isResult && message is not null
                    ? message.Handle
                    : current.ErrorHandle
            });
            CommitCurrentLocked(
                accepted
                    ? "effect-completion-publication"
                    : "late-effect-observation-publication",
                candidatePayloads,
                candidateReceipts);
            SignalPlanner();
        }
        finally
        {
            mutationGate.Release();
        }
    }

    /// <summary>
    /// 结算证据和给用户看的文本是两件事，这里把它们分开。
    ///
    /// 证据是收据合同要求的：成功必须有观察句柄、失败必须有错误句柄
    /// （见 <c>HostManagerOperationEffectReceiptTable.HasCanonicalEvidenceShape</c>），
    /// 所以这条载荷一定要写。文本则是可选的 —— 有些结算只有结论，没有能本地化的话可说；
    /// 这时证据里记的是结算结论的稳定标识，它只用于证明「这次结算被观察到了」，不进界面。
    ///
    /// 给用户看的结果或错误文本，只在真有一句话时才把操作记录的句柄和 ValidMask 指过去；
    /// 投影端本来就按 ResultValid/ErrorValid 读取，所以「没有文本」是协议里已有的合法状态。
    /// </summary>
    private static string OutcomeEvidenceToken(HostManagerOperationEffectOutcome outcome)
        => outcome switch
        {
            HostManagerOperationEffectOutcome.Succeeded => "succeeded",
            HostManagerOperationEffectOutcome.RetryableFailure => "retryable-failure",
            HostManagerOperationEffectOutcome.TerminalFailure => "terminal-failure",
            HostManagerOperationEffectOutcome.Canceled => "canceled",
            HostManagerOperationEffectOutcome.Uncertain => "uncertain",
            _ => "unspecified"
        };

    private static string OutcomeEvidenceToken(
        HostManagerOperationEffectReceiptOutcome outcome)
        => outcome switch
        {
            HostManagerOperationEffectReceiptOutcome.Succeeded => "succeeded",
            HostManagerOperationEffectReceiptOutcome.EffectNotObserved => "effect-not-observed",
            HostManagerOperationEffectReceiptOutcome.RetryableFailure => "retryable-failure",
            HostManagerOperationEffectReceiptOutcome.TerminalFailure => "terminal-failure",
            HostManagerOperationEffectReceiptOutcome.Canceled => "canceled",
            HostManagerOperationEffectReceiptOutcome.Uncertain => "uncertain",
            _ => "unspecified"
        };

    private HostManagerOperationPayloadEntry AddTextLocked(
        HostManagerOperationPayloadCatalog candidatePayloads,
        uint schema,
        HostManagerOperationPayloadRole role,
        HostManagerOperationActionTicket ticket,
        string text)
        => candidatePayloads.Add(
                schema,
                HostManagerOperationRequestSchemas.Version,
                role,
                workspace!.SessionInstanceId,
                ticket.Action.OperationId,
                ticket.Action.AttemptToken,
                HostManagerOperationRequestCodec.EncodeText(text));

    private async Task ApplyActionFeedbackAsync(
        HostManagerOperationActionTicket ticket,
        NativeOperationActionFeedbackOutcome feedback,
        HostManagerOperationEffectReceiptOutcome receiptOutcome,
        string message,
        CancellationToken ownerStopping,
        HostManagerOperationEffectReceipt? recoveredPrior = null)
    {
        await mutationGate.WaitAsync(ownerStopping).ConfigureAwait(false);
        try
        {
            if (!TryRequireMutableLocked())
            {
                return;
            }
            var candidatePayloads = payloads.Clone();
            var candidateReceipts = receipts.Clone();
            var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var isResult =
                receiptOutcome == HostManagerOperationEffectReceiptOutcome.Succeeded;
            var hasMessage = !string.IsNullOrWhiteSpace(message);
            var evidenceText = hasMessage ? message : OutcomeEvidenceToken(receiptOutcome);
            var messagePayload = !isResult || hasMessage
                ? AddTextLocked(
                    candidatePayloads,
                    isResult
                        ? HostManagerOperationRequestSchemas.OperationResult
                        : HostManagerOperationRequestSchemas.OperationError,
                    isResult
                        ? HostManagerOperationPayloadRole.Result
                        : HostManagerOperationPayloadRole.Error,
                    ticket,
                    evidenceText)
                : null;
            var observation = isResult
                ? AddTextLocked(
                    candidatePayloads,
                    HostManagerOperationRequestSchemas.EffectObservation,
                    HostManagerOperationPayloadRole.EffectObservation,
                    ticket,
                    evidenceText)
                : null;
            var input = new NativeOperationActionFeedbackInput
            {
                ActionId = ticket.Action.ActionId,
                PlanEpoch = ticket.Action.PlanEpoch,
                OperationId = ticket.Action.OperationId,
                AttemptToken = ticket.Action.AttemptToken,
                ActionKind = ticket.Action.Kind,
                Outcome = (uint)feedback,
                ObservedUtcMilliseconds = now,
                ObservedMonotonicMilliseconds =
                    NativeOperationCoordinatorWorkspace.MonotonicMilliseconds(),
                ResultHandle = isResult && hasMessage && messagePayload is not null
                    ? messagePayload.Handle
                    : default,
                ErrorHandle = !isResult && hasMessage && messagePayload is not null
                    ? messagePayload.Handle
                    : default,
                ValidMask = !hasMessage || messagePayload is null
                    ? 0
                    : (ulong)(isResult
                        ? NativeOperationResultValidity.Result
                        : NativeOperationResultValidity.Error),
                Flags = 0
            };
            var accepted = workspace!.ApplyActionFeedbackLocked(ref input);
            var current = candidateReceipts.Require(ticket.ReceiptId);
            candidateReceipts.Upsert(current with
            {
                State = receiptOutcome == HostManagerOperationEffectReceiptOutcome.Uncertain
                    ? HostManagerOperationEffectReceiptState.Uncertain
                    : HostManagerOperationEffectReceiptState.Settled,
                Outcome = receiptOutcome,
                ReceiptRevision = current.ReceiptRevision + 1,
                UpdatedUtcMilliseconds = now,
                ObservationHandle = observation is not null
                    ? observation.Handle
                    : current.ObservationHandle,
                ErrorHandle = !isResult && messagePayload is not null
                    ? messagePayload.Handle
                    : current.ErrorHandle
            });
            if (recoveredPrior is not null && receiptOutcome != HostManagerOperationEffectReceiptOutcome.Uncertain)
            {
                var prior = candidateReceipts.Require(recoveredPrior.ReceiptId);
                RequireExactReadbackBinding(ticket, prior, recoveredPrior.AttemptToken);
                if (prior.State != HostManagerOperationEffectReceiptState.Settled)
                {
                    // Conclusive readback closes this exact old effect attempt; its identity and original request evidence are retained.
                    var priorEvidence = candidatePayloads.Add(
                        isResult ? HostManagerOperationRequestSchemas.EffectObservation : HostManagerOperationRequestSchemas.OperationError,
                        HostManagerOperationRequestSchemas.Version,
                        isResult ? HostManagerOperationPayloadRole.EffectObservation : HostManagerOperationPayloadRole.Error,
                        prior.SessionInstanceId, prior.OperationId, prior.AttemptToken,
                        HostManagerOperationRequestCodec.EncodeText(OutcomeEvidenceToken(receiptOutcome)));
                    candidateReceipts.Upsert(prior with
                    {
                        State = HostManagerOperationEffectReceiptState.Settled,
                        Outcome = receiptOutcome,
                        ReceiptRevision = checked(prior.ReceiptRevision + 1),
                        UpdatedUtcMilliseconds = now,
                        ObservationHandle = isResult ? priorEvidence.Handle : default,
                        ErrorHandle = isResult ? default : priorEvidence.Handle
                    });
                }
            }
            CommitCurrentLocked(
                accepted
                    ? "action-feedback-publication"
                    : "late-action-observation-publication",
                candidatePayloads,
                candidateReceipts);
            SignalPlanner();
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private Task SettleExpiredTicketAsync(
        HostManagerOperationActionTicket ticket,
        CancellationToken ownerStopping)
        => (NativeOperationActionKind)ticket.Action.Kind switch
        {
            NativeOperationActionKind.Start => ApplyActionFeedbackAsync(
                ticket,
                NativeOperationActionFeedbackOutcome.StartRetryableFailure,
                HostManagerOperationEffectReceiptOutcome.EffectNotObserved,
                HostManagerOperationFeedback.Reason(ResourceManager.App.Domain.Messages.BackendMessageCodes.Operation.NoExecutionEvidence),
                ownerStopping),
            NativeOperationActionKind.Cancel => ApplyActionFeedbackAsync(
                ticket,
                NativeOperationActionFeedbackOutcome.CancelRetryableFailure,
                HostManagerOperationEffectReceiptOutcome.EffectNotObserved,
                HostManagerOperationFeedback.Reason(ResourceManager.App.Domain.Messages.BackendMessageCodes.Operation.NoExecutionEvidence),
                ownerStopping),
            _ => ApplyActionFeedbackAsync(
                ticket,
                NativeOperationActionFeedbackOutcome.RecoveredUncertain,
                HostManagerOperationEffectReceiptOutcome.Uncertain,
                HostManagerOperationFeedback.Reason(ResourceManager.App.Domain.Messages.BackendMessageCodes.Operation.OutcomeUncertain),
                ownerStopping)
        };

    private async Task LatchWorkerFaultAsync(Exception error, string stage)
    {
        try
        {
            await mutationGate.WaitAsync(CancellationToken.None)
                .ConfigureAwait(false);
            try
            {
                if (!retiring && !disposed && !persistenceFaulted)
                {
                    LatchPersistenceFaultLocked(error, stage);
                }
            }
            finally
            {
                mutationGate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static NativeOperationCompletionOutcome MapCompletionOutcome(
        HostManagerOperationEffectOutcome outcome)
        => outcome switch
        {
            HostManagerOperationEffectOutcome.Succeeded =>
                NativeOperationCompletionOutcome.Succeeded,
            HostManagerOperationEffectOutcome.RetryableFailure =>
                NativeOperationCompletionOutcome.RetryableFailure,
            HostManagerOperationEffectOutcome.TerminalFailure =>
                NativeOperationCompletionOutcome.TerminalFailure,
            HostManagerOperationEffectOutcome.Canceled =>
                NativeOperationCompletionOutcome.Canceled,
            HostManagerOperationEffectOutcome.Uncertain =>
                NativeOperationCompletionOutcome.StateUncertain,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

    private static HostManagerOperationEffectReceiptOutcome MapReceiptOutcome(
        HostManagerOperationEffectOutcome outcome)
        => outcome switch
        {
            HostManagerOperationEffectOutcome.Succeeded =>
                HostManagerOperationEffectReceiptOutcome.Succeeded,
            HostManagerOperationEffectOutcome.RetryableFailure =>
                HostManagerOperationEffectReceiptOutcome.RetryableFailure,
            HostManagerOperationEffectOutcome.TerminalFailure =>
                HostManagerOperationEffectReceiptOutcome.TerminalFailure,
            HostManagerOperationEffectOutcome.Canceled =>
                HostManagerOperationEffectReceiptOutcome.Canceled,
            HostManagerOperationEffectOutcome.Uncertain =>
                HostManagerOperationEffectReceiptOutcome.Uncertain,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

    private static uint EffectKindCode(string kind)
        => kind switch
        {
            HostManagerOperationKinds.ComponentDownload => 1,
            HostManagerOperationKinds.ComponentInstall => 2,
            HostManagerOperationKinds.DependencyDownload => 3,
            HostManagerOperationKinds.DependencyLaunchInstaller => 4,
            HostManagerOperationKinds.SoftwareUninstall => 5,
            HostManagerOperationKinds.DiskUsageScan => 9,
            _ => throw new InvalidDataException(
                "The operation effect kind is unknown.")
        };

    private void RequireExactReadbackBinding(
        HostManagerOperationActionTicket ticket,
        HostManagerOperationEffectReceipt evidence,
        NativeOperationHandle128 effectAttemptToken)
    {
        if (workspace is null
            || evidence.SessionInstanceId != workspace.SessionInstanceId
            || evidence.OperationId != ticket.Action.OperationId
            || evidence.AttemptToken != effectAttemptToken
            || evidence.EffectKind != EffectKindCode(ticket.Kind))
        {
            throw new InvalidDataException(
                "The operation effect receipt does not exactly bind the recovery ticket.");
        }
        var expectedBefore = payloads.Require(evidence.ExpectedBeforeHandle);
        if (expectedBefore.SchemaId
                != HostManagerOperationRequestSchemas.EffectExpectedBefore
            || expectedBefore.SchemaVersion
                != HostManagerOperationRequestSchemas.Version
            || expectedBefore.Role
                != HostManagerOperationPayloadRole.EffectExpectedBefore
            || expectedBefore.SessionInstanceId != evidence.SessionInstanceId
            || expectedBefore.OperationId != evidence.OperationId
            || expectedBefore.AttemptToken != evidence.AttemptToken
            || HostManagerOperationRequestCodec.DecodeEffectExpectedBefore(
                expectedBefore.Payload) != ticket.Request.Handle)
        {
            throw new InvalidDataException(
                "The operation effect receipt request evidence is non-canonical.");
        }
    }

    private static NativeOperationHandle128 CreateReceiptId(
        NativeOperationHandle128 sessionInstanceId,
        in NativeOperationActionOutput action,
        uint effectKind,
        uint effectPhase)
    {
        Span<byte> identity = stackalloc byte[104];
        HostManagerOperationPayloadCatalog.WriteHandle(
            identity,
            sessionInstanceId);
        HostManagerOperationPayloadCatalog.WriteHandle(
            identity[16..],
            action.OperationId);
        HostManagerOperationPayloadCatalog.WriteHandle(
            identity[32..],
            action.AttemptToken);
        BinaryPrimitives.WriteUInt64LittleEndian(identity[48..], action.ActionId);
        BinaryPrimitives.WriteUInt64LittleEndian(identity[56..], action.PlanEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(
            identity[64..],
            action.ConfigurationGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(identity[72..], action.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(identity[76..], effectKind);
        BinaryPrimitives.WriteUInt32LittleEndian(identity[80..], effectPhase);
        BinaryPrimitives.WriteUInt32LittleEndian(identity[84..], 0);
        identity[88..].Clear();
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(identity, digest);
        var result = new NativeOperationHandle128(
            BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]));
        if (result.IsZero)
        {
            result = new NativeOperationHandle128(
                BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
        }
        return result.IsZero
            ? throw new CryptographicException(
                "The operation effect receipt identity is zero.")
            : result;
    }
}
