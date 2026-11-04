using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private async Task<NativeTransactionExecutionBatch> ExecuteNativeTransactionsAsync(
        HostManagerCycleEffectPermit permit,
        HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
        NativeSmartCoordinatorWorkspace workspace,
        uint requiredFeedbackCount,
        HostManagerSample sample,
        HostManagerTransactionJournalAdmission admission,
        DateTimeOffset cycleStartedAt,
        CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.NativeActionTransaction);
        var settlements = new List<NativeTransactionSettlement>();
        uint feedbackCount = 0;
        uint successfulFeedbackCount = 0;
        var stopExternalEffects = false;

        for (uint index = 0; index < workspace.PlannedActionCount; index++)
        {
            var action = workspace.GetPlannedAction(index);
            if (!action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.RequiresFeedback))
            {
                continue;
            }

            NativeTransactionActionResult result;
            if (stopExternalEffects)
            {
                result = NativeTransactionActionResult.Direct(
                    CreateNativeFeedback(
                        action,
                        NativeSmartCoordinatorFeedbackStatus.Skipped,
                        durableTimeSource.NextUtc()));
            }
            else if (!HostManagerProcessEffectValidationCyclePolicy.AllowsNativeAction(
                    processEffectValidationScope,
                    action.Scope))
            {
                result = NativeTransactionActionResult.Direct(
                    CreateNativeFeedback(
                        action,
                        NativeSmartCoordinatorFeedbackStatus.Skipped,
                        durableTimeSource.NextUtc()));
            }
            else if (action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy &&
                (action.FromProcessGrade == NativeSmartCoordinatorProcessGrade.Level4 ||
                    action.ToProcessGrade == NativeSmartCoordinatorProcessGrade.Level4))
            {
                result = NativeTransactionActionResult.Direct(
                    CreateNativeFeedback(
                        action,
                        NativeSmartCoordinatorFeedbackStatus.Rejected,
                        durableTimeSource.NextUtc(),
                        actualProcessGrade: action.FromProcessGrade));
            }
            else if (action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy
                && action.Disposition != NativeSmartCoordinatorActionDisposition.Restore
                && !processEffectValidationScope.Allows(
                    new HostManagerComputeProcessIdentity(
                        checked((int)action.ProcessId),
                        action.ProcessStartKey)))
            {
                result = NativeTransactionActionResult.Direct(
                    CreateNativeFeedback(
                        action,
                        NativeSmartCoordinatorFeedbackStatus.Skipped,
                        durableTimeSource.NextUtc()));
            }
            else
            {
                result = await ExecuteNativeTransactionAsync(
                    permit,
                    processEffectValidationScope,
                    action,
                    sample,
                    admission,
                    cancellationToken);
            }

            ValidateNativeFeedback(
                result.Feedback,
                action,
                cycleStartedAt.ToUnixTimeMilliseconds());
            workspace.SetFeedback(feedbackCount++, result.Feedback);
            if (result.Feedback.Status == NativeSmartCoordinatorFeedbackStatus.Succeeded)
            {
                successfulFeedbackCount++;
            }
            if (result.Settlement is { } settlement)
            {
                settlements.Add(settlement);
            }
            if (result.Feedback.Status is NativeSmartCoordinatorFeedbackStatus.StateUncertain
                or NativeSmartCoordinatorFeedbackStatus.OwnershipLost)
            {
                stopExternalEffects = true;
            }
        }

        if (feedbackCount != requiredFeedbackCount)
        {
            throw new InvalidDataException(
                "The Host Manager transaction executor did not produce one feedback row for every reservation.");
        }
        return new NativeTransactionExecutionBatch(
            feedbackCount,
            successfulFeedbackCount,
            settlements);
    }

    private async Task<NativeTransactionActionResult> ExecuteNativeTransactionAsync(
        HostManagerCycleEffectPermit permit,
        HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
        NativeSmartCoordinatorAction action,
        HostManagerSample? sample,
        HostManagerTransactionJournalAdmission admission,
        CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.NativeActionTransaction);
        var primary = CreateAppliedOwnershipPrimary(in action);
        if (await SkipPendingJournalTargetAsync(admission, primary,
                (NativeTransactionJournalDomain)action.DomainMask, cancellationToken))
        {
            return NativeTransactionActionResult.Direct(CreateNativeFeedback(
                action, NativeSmartCoordinatorFeedbackStatus.Skipped,
                durableTimeSource.NextUtc()));
        }
        var ownershipRead = await nativeActionTransactions.AppliedOwnership.GetAsync(
            primary,
            cancellationToken);
        NativeAppliedOwnershipRecord? ownership = ownershipRead.Status switch
        {
            NativeAppliedOwnershipStatus.Ok => ownershipRead.Record,
            NativeAppliedOwnershipStatus.NoData => null,
            _ => throw new InvalidDataException(
                $"Applied ownership read failed with {ownershipRead.Status}.")
        };

        if (ownership is null &&
            (action.Disposition == NativeSmartCoordinatorActionDisposition.Restore ||
                ActionFromGradeIsOwned(in action)))
        {
            return NativeTransactionActionResult.Direct(
                CreateNativeFeedback(
                    action,
                    NativeSmartCoordinatorFeedbackStatus.OwnershipLost,
                    durableTimeSource.NextUtc()));
        }
        var journalPlan = nativeActionTransactions.CurrentJournalPlan;
        var now = CurrentUtcMilliseconds();
        var recoveryDeadline = CheckedAdd(
            now,
            checked((ulong)journalPlan.HotPublish.RecoveryDeadlineMilliseconds));
        var actionDeadline = DateTimeOffset.FromUnixTimeMilliseconds(
            checked((long)CheckedAdd(
                now,
                checked((ulong)appliedNativeRuntimePlan!.SmartCoordinator.HotPublish
                    .ReservationTimeoutMilliseconds))));
        var journalSnapshot = await admission.ReadSnapshotAsync(cancellationToken);

        NativeTransactionJournalPayloadReference payloadReference = default;
        NativeTransactionJournalPayloadBinding payloadBinding;
        NativeTransactionJournalPayloadProvenance payloadProvenance;
        byte[] payload;
        var persistedForThisAction = false;
        var prepareCompleted = false;
        if (ownership is { } currentOwnership)
        {
            payloadReference = HostManagerAppliedOwnershipProjection.CreatePayloadReference(
                in currentOwnership);
            payloadBinding = HostManagerAppliedOwnershipProjection.CreatePayloadBinding(
                in currentOwnership);
            payloadProvenance = NativeTransactionJournalPayloadProvenance.Create(payloadBinding);
            payload = await admission.ReadPayloadAsync(
                payloadReference,
                payloadBinding,
                cancellationToken);
        }
        else
        {
            var capture = await CaptureTransactionPayloadAsync(
                action,
                sample ?? throw new InvalidOperationException(
                    "A native apply cannot capture a rollback payload without a current Host Manager sample."),
                journalPlan.Recreate.PayloadByteBudget,
                actionDeadline,
                cancellationToken);
            if (capture.Status != TransactionEffectStatus.Succeeded)
            {
                return NativeTransactionActionResult.Direct(
                    CreateUnchangedFeedback(
                        in action,
                        capture.Status,
                        capture.SystemError,
                        durableTimeSource.NextUtc()));
            }

            payload = capture.Payload;
            payloadBinding = HostManagerTransactionJournalProjection.CreatePayloadBinding(
                in action,
                journalSnapshot.Header.JournalInstanceLow,
                journalSnapshot.Header.JournalInstanceHigh,
                nativeHostSessionIncarnation,
                now,
                checked((uint)journalPlan.HotPublish.MaximumRecoveryAttempts),
                recoveryDeadline);
            payloadProvenance = NativeTransactionJournalPayloadProvenance.Create(payloadBinding);
        }

        if (!permit.TryReserveExactNewPointOfNoReturn(
                1,
                out var effectReservation)
            || effectReservation is null)
        {
            return NativeTransactionActionResult.Direct(
                CreateNativeFeedback(
                    action,
                    NativeSmartCoordinatorFeedbackStatus.Skipped,
                    durableTimeSource.NextUtc()));
        }
        using var effectReservationScope = effectReservation;
        HostManagerCycleEffectReservation? restoreReservation = null;
        if (ownership is not null
            && !permit.TryReserveOwnedNativeRestore(out restoreReservation))
        {
            return NativeTransactionActionResult.Direct(
                CreateNativeFeedback(
                    action,
                    NativeSmartCoordinatorFeedbackStatus.Skipped,
                    durableTimeSource.NextUtc()));
        }
        using var restoreReservationScope = restoreReservation;
        if (ownership is null)
        {
            payloadReference = await admission.PersistPayloadAsync(
                payloadBinding,
                payload,
                cancellationToken);
            persistedForThisAction = true;
        }

        HostManagerProcessEffectValidationAdmissionPermit? validationPermit = null;
        var validationAdmitted = true;
        if (action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy)
        {
            if (action.Disposition == NativeSmartCoordinatorActionDisposition.Restore
                && ownership is { } restoreOwnership)
            {
                var sourceIdentity = CreateProcessEffectSourceIdentity(in restoreOwnership);
                validationAdmitted = TryBeginProcessEffectRecoveryAdmission(
                    HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
                    "owned-restore-pre-ponr",
                    checked((int)action.ProcessId),
                    action.ProcessStartKey,
                    sourceIdentity,
                    out validationPermit);
            }
            else
            {
                validationAdmitted = TryBeginProcessEffectAdmission(
                    processEffectValidationScope,
                    HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction,
                    "native-process-policy-pre-ponr",
                    checked((int)action.ProcessId),
                    action.ProcessStartKey,
                    out validationPermit);
            }
        }
        if (!validationAdmitted)
        {
            if (persistedForThisAction)
            {
                _ = await admission.DeletePayloadAsync(
                    payloadReference,
                    payloadBinding,
                    CancellationToken.None);
            }
            return NativeTransactionActionResult.Direct(
                CreateNativeFeedback(
                    action,
                    NativeSmartCoordinatorFeedbackStatus.Skipped,
                    durableTimeSource.NextUtc()));
        }

        NativeTransactionJournalRecord record;
        var prepareRejectedWithoutMutation = false;
        try
        {
            var prepare = HostManagerTransactionJournalProjection.CreatePrepare(
                in action,
                nativeHostSessionIncarnation,
                journalSnapshot.Header.JournalRevision,
                in payloadReference,
                in payloadProvenance,
                now,
                checked((uint)journalPlan.HotPublish.MaximumRecoveryAttempts),
                recoveryDeadline);
            if (validationPermit is not null
                && !TryDeclareProcessEffectAdmission(
                    validationPermit,
                    prepare.Identity))
            {
                throw new InvalidOperationException(
                    "The native process-policy validation admission target was not durably declared.");
            }
            var prepareStatus = await admission.PrepareAsync(
                prepare,
                cancellationToken);
            prepareRejectedWithoutMutation =
                prepareStatus != NativeTransactionJournalStatus.Ok;
            if (prepareStatus == NativeTransactionJournalStatus.InvalidTransition
                && await SkipPendingJournalTargetAsync(admission, primary,
                    (NativeTransactionJournalDomain)action.DomainMask, cancellationToken))
            {
                if (persistedForThisAction)
                {
                    _ = await admission.DeletePayloadAsync(payloadReference, payloadBinding, CancellationToken.None);
                }
                return NativeTransactionActionResult.Direct(CreateNativeFeedback(
                    action, NativeSmartCoordinatorFeedbackStatus.Skipped, durableTimeSource.NextUtc()));
            }
            RequireJournalStatus(prepareStatus, "prepare");
            prepareCompleted = true;
            record = await RequireJournalRecordAsync(
                admission,
                prepare.Identity,
                cancellationToken);
        }
        catch (Exception exception)
        {
            MarkProcessEffectAdmissionUnsettled(
                validationPermit,
                "native-process-policy-prepare-failed");
            if (NativeTransactionJournalPayloadPreparePolicy.ShouldDeleteAfterFailure(
                    persistedForThisAction,
                    prepareCompleted,
                    prepareRejectedWithoutMutation,
                    exception))
            {
                _ = await admission.DeletePayloadAsync(
                    payloadReference,
                    payloadBinding,
                    CancellationToken.None);
            }
            throw;
        }
        if (validationPermit is not null
            && !TryCommitProcessEffectAdmission(validationPermit, record.Identity))
        {
            throw new InvalidOperationException(
                "The native process-policy validation admission was not durably handed off.");
        }

        NativeAppliedOwnershipTransitionInput? plannedTransition = null;
        var previousEffectRestored = false;
        if (ownership is { } existing)
        {
            var preparedJournal = await admission.ReadSnapshotAsync(cancellationToken);
            var preparedJournalHeader = preparedJournal.Header;
            var transitionEvidence = HostManagerAppliedOwnershipProjection.CreateTransitionEvidence(
                in preparedJournalHeader,
                in record,
                in action,
                in payloadReference);
            var transitionPlan = await nativeActionTransactions.AppliedOwnership.PlanTransitionAsync(
                existing.Primary,
                transitionEvidence.Binding,
                transitionEvidence.Payload,
                cancellationToken);
            RequireOwnershipStatus(transitionPlan.Status, "plan-transition");
            plannedTransition = transitionPlan.Transition;

            restoreReservation!.EnterPointOfNoReturn();
            var restore = await RestoreTransactionPayloadAsync(
                action,
                payload,
                journalPlan.Recreate.PayloadByteBudget,
                actionDeadline,
                cancellationToken);
            var restoreFailure = await HandlePreApplyRestoreResultAsync(
                action,
                admission,
                record,
                restore,
                cancellationToken);
            if (restoreFailure is not null)
            {
                return restoreFailure.Value;
            }

            record = await MutateJournalRecordAsync(
                admission,
                record,
                NativeTransactionJournalMutationEvent.ConfirmPreviousEffectRestored,
                restore.SystemStatus,
                restore.SystemError,
                cancellationToken);
            previousEffectRestored = true;
        }

        TransactionEffectResult effect;
        if (plannedTransition is { } compositeTransition)
        {
            if (compositeTransition.NewCurrentGrades.ValidMask != 0)
            {
                effectReservation.EnterPointOfNoReturn();
            }
            effect = await ApplyPlannedTransitionPayloadAsync(
                action,
                sample,
                payload,
                compositeTransition.NewCurrentGrades,
                journalPlan.Recreate.PayloadByteBudget,
                actionDeadline,
                cancellationToken);
        }
        else if (action.Disposition == NativeSmartCoordinatorActionDisposition.Restore)
        {
            effectReservation.EnterPointOfNoReturn();
            effect = await RestoreTransactionPayloadAsync(
                action,
                payload,
                journalPlan.Recreate.PayloadByteBudget,
                actionDeadline,
                cancellationToken);
        }
        else
        {
            effectReservation.EnterPointOfNoReturn();
            effect = await ApplyTransactionPayloadAsync(
                action,
                sample ?? throw new InvalidOperationException(
                    "A native apply cannot execute without a current Host Manager sample."),
                payload,
                journalPlan.Recreate.PayloadByteBudget,
                actionDeadline,
                cancellationToken);
        }

        if (effect.Status == TransactionEffectStatus.ProcessExited)
        {
            return await CompleteExitedProcessTransactionAsync(
                action,
                admission,
                record,
                effect,
                cancellationToken);
        }
        if (effect.Status is TransactionEffectStatus.StateUncertain or
            TransactionEffectStatus.OwnershipLost)
        {
            await ApplyNonTerminalRecoveryEvidenceAsync(
                admission,
                record,
                effect,
                journalPlan.HotPublish.RetryDelayMilliseconds,
                cancellationToken);
            return NativeTransactionActionResult.Direct(
                CreateUnchangedFeedback(
                    in action,
                    effect.Status,
                    effect.SystemError,
                    durableTimeSource.NextUtc(record.UpdatedAtUtcMilliseconds)));
        }

        record = await MutateJournalRecordAsync(
            admission,
            record,
            NativeTransactionJournalMutationEvent.ConfirmEffectObserved,
            effect.SystemStatus,
            effect.SystemError,
            cancellationToken);

        var ownershipRemains = ownership is not null;
        if (effect.Status == TransactionEffectStatus.Succeeded)
        {
            var ownershipSnapshot = await nativeActionTransactions.AppliedOwnership
                .ReadSnapshotAsync(cancellationToken);
            var journalAfterEffect = await admission.ReadSnapshotAsync(cancellationToken);
            var journalHeader = journalAfterEffect.Header;
            if (plannedTransition is { } transitionPlan)
            {
                var transition = HostManagerAppliedOwnershipProjection.CompleteTransition(
                    in transitionPlan,
                    in record,
                    CurrentUtcMilliseconds(record.UpdatedAtUtcMilliseconds));
                RequireOwnershipStatus(
                    await nativeActionTransactions.AppliedOwnership.TransitionAsync(
                        transition,
                        cancellationToken),
                    "transition");
                ownershipRemains = transition.NewCurrentGrades.ValidMask != 0;
            }
            else
            {
                var promote = HostManagerAppliedOwnershipProjection.CreatePromote(
                    in journalHeader,
                    in record,
                    in action,
                    in payloadReference,
                    in payloadBinding,
                    ownershipSnapshot.Header.LedgerRevision,
                    CurrentUtcMilliseconds(record.UpdatedAtUtcMilliseconds));
                RequireOwnershipStatus(
                    await nativeActionTransactions.AppliedOwnership.PromoteAsync(
                        promote,
                        cancellationToken),
                    "promote");
                ownershipRemains = true;
            }
        }
        else if (previousEffectRestored && ownership is not null)
        {
            var currentRead = await nativeActionTransactions.AppliedOwnership.GetAsync(
                primary,
                cancellationToken);
            if (currentRead.Status != NativeAppliedOwnershipStatus.Ok)
            {
                throw new InvalidDataException(
                    $"Applied ownership baseline reconciliation failed with {currentRead.Status}.");
            }
            var ownershipSnapshot = await nativeActionTransactions.AppliedOwnership
                .ReadSnapshotAsync(cancellationToken);
            var reconciledOwnership = currentRead.Record;
            var remove = HostManagerAppliedOwnershipProjection.CreateRecoveryRemove(
                in reconciledOwnership,
                ownershipSnapshot.Header.LedgerRevision,
                CurrentUtcMilliseconds(reconciledOwnership.UpdatedAtUtcMilliseconds));
            RequireOwnershipStatus(
                await nativeActionTransactions.AppliedOwnership.RemoveAsync(
                    remove,
                    cancellationToken),
                "remove-restored-baseline");
            ownershipRemains = false;
        }

        var feedback = effect.Status switch
        {
            TransactionEffectStatus.Succeeded => CreateSucceededFeedback(
                in action,
                durableTimeSource.NextUtc(record.UpdatedAtUtcMilliseconds)),
            TransactionEffectStatus.FailedUnchanged => CreateUnchangedFeedback(
                in action,
                ResolveTerminalFeedbackEffectStatus(
                    effect.Status,
                    previousEffectRestored),
                effect.SystemError,
                durableTimeSource.NextUtc(record.UpdatedAtUtcMilliseconds)),
            TransactionEffectStatus.Rejected => CreateUnchangedFeedback(
                in action,
                ResolveTerminalFeedbackEffectStatus(
                    effect.Status,
                    previousEffectRestored),
                effect.SystemError,
                durableTimeSource.NextUtc(record.UpdatedAtUtcMilliseconds)),
            _ => throw new InvalidOperationException(
                $"Effect status {effect.Status} cannot be staged as terminal feedback.")
        };
        ValidateNativeFeedback(
            feedback,
            action,
            checked((long)record.PreparedAtUtcMilliseconds));
        var feedbackJournal = await admission.ReadSnapshotAsync(cancellationToken);
        var stage = HostManagerTransactionJournalProjection.CreateFeedback(
            in record,
            feedbackJournal.Header.JournalRevision,
            nativeHostSessionIncarnation,
            in feedback);
        RequireJournalStatus(
            await admission.StageFeedbackAsync(stage, cancellationToken),
            "stage-feedback");
        var staged = await RequireJournalRecordAsync(
            admission,
            record.Identity,
            cancellationToken);
        return new NativeTransactionActionResult(
            feedback,
            new NativeTransactionSettlement(
                staged.Identity,
                !ownershipRemains,
                payloadReference,
                payloadBinding));
    }

    private async Task CommitAcceptedNativeTransactionsAsync(
        HostManagerTransactionJournalAdmission admission,
        IReadOnlyList<NativeTransactionSettlement> settlements,
        CancellationToken cancellationToken)
    {
        foreach (var settlement in settlements)
        {
            var snapshot = await admission.ReadSnapshotAsync(cancellationToken);
            var record = await RequireJournalRecordAsync(
                admission,
                settlement.Identity,
                cancellationToken);
            var acknowledgedAt = CurrentUtcMilliseconds(record.UpdatedAtUtcMilliseconds);
            var ack = HostManagerTransactionJournalProjection.CreateAck(
                in record,
                snapshot.Header.JournalRevision,
                NativeTransactionJournalAckResult.Accepted,
                acknowledgedAt,
                0,
                0,
                0);
            RequireJournalStatus(
                await admission.AcknowledgeAsync(ack, cancellationToken),
                "acknowledge");
            if (settlement.DeletePayloadAfterAcknowledgement)
            {
                _ = await admission.DeletePayloadAsync(
                    settlement.PayloadReference,
                    settlement.PayloadBinding,
                    cancellationToken);
            }
        }
    }

    private async Task<NativeTransactionActionResult?> HandlePreApplyRestoreResultAsync(
        NativeSmartCoordinatorAction action,
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalRecord record,
        TransactionEffectResult restore,
        CancellationToken cancellationToken)
    {
        if (restore.Status == TransactionEffectStatus.Succeeded)
        {
            return null;
        }
        if (restore.Status == TransactionEffectStatus.ProcessExited)
        {
            return await CompleteExitedProcessTransactionAsync(
                action,
                admission,
                record,
                restore,
                cancellationToken);
        }
        if (restore.Status is TransactionEffectStatus.StateUncertain or
            TransactionEffectStatus.OwnershipLost)
        {
            await ApplyNonTerminalRecoveryEvidenceAsync(
                admission,
                record,
                restore,
                nativeActionTransactions.CurrentJournalPlan.HotPublish.RetryDelayMilliseconds,
                cancellationToken);
            return NativeTransactionActionResult.Direct(
                CreateUnchangedFeedback(
                    in action,
                    restore.Status,
                    restore.SystemError,
                    durableTimeSource.NextUtc(record.UpdatedAtUtcMilliseconds)));
        }

        var observed = await MutateJournalRecordAsync(
            admission,
            record,
            NativeTransactionJournalMutationEvent.ConfirmEffectObserved,
            restore.SystemStatus,
            restore.SystemError,
            cancellationToken);
        var feedback = CreateUnchangedFeedback(
            in action,
            restore.Status,
            restore.SystemError,
            durableTimeSource.NextUtc(observed.UpdatedAtUtcMilliseconds));
        var snapshot = await admission.ReadSnapshotAsync(cancellationToken);
        var stage = HostManagerTransactionJournalProjection.CreateFeedback(
            in observed,
            snapshot.Header.JournalRevision,
            nativeHostSessionIncarnation,
            in feedback);
        RequireJournalStatus(
            await admission.StageFeedbackAsync(stage, cancellationToken),
            "stage-pre-apply-restore-feedback");
        var staged = await RequireJournalRecordAsync(
            admission,
            observed.Identity,
            cancellationToken);
        return new NativeTransactionActionResult(
            feedback,
            new NativeTransactionSettlement(
                staged.Identity,
                false,
                default,
                default));
    }

    private async Task<NativeTransactionActionResult> CompleteExitedProcessTransactionAsync(
        NativeSmartCoordinatorAction action,
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalRecord record,
        TransactionEffectResult effect,
        CancellationToken cancellationToken)
    {
        if (action.Scope != NativeSmartCoordinatorActionScope.ProcessPolicy)
        {
            throw new InvalidDataException(
                "Only a process-policy transaction can report ProcessExited.");
        }

        await ApplyRecoveryOutcomeAsync(
            admission,
            record,
            NativeTransactionJournalRecoveryOutcome.ProcessExited,
            effect.SystemStatus,
            effect.SystemError,
            cancellationToken);
        var transitioned = await RequireJournalRecordAsync(
            admission,
            record.Identity,
            cancellationToken);
        await RemoveRecoveredOwnershipAsync(transitioned, cancellationToken);
        await CompleteAuthoritativeResyncAsync(
            admission,
            transitioned,
            cancellationToken);
        authoritativeAppliedFactsRequired = true;

        var payloadReference =
            HostManagerTransactionJournalProjection.CreatePayloadReference(in record);
        var payloadProvenance =
            HostManagerTransactionJournalProjection.CreatePayloadProvenance(in record);
        _ = await admission.DeletePayloadAsync(
            payloadReference,
            payloadProvenance,
            cancellationToken);
        if (admission.PayloadReconciliationRequired)
        {
            var journalSnapshot = await admission.ReadSnapshotAsync(cancellationToken);
            await ReconcileNativePayloadsIfRequiredAsync(
                admission,
                journalSnapshot,
                cancellationToken);
        }

        return NativeTransactionActionResult.Direct(
            CreateProcessExitedFeedback(
                in action,
                effect.SystemError,
                durableTimeSource.NextUtc(record.UpdatedAtUtcMilliseconds)));
    }

    private async Task<NativeTransactionJournalRecord> MutateJournalRecordAsync(
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalRecord record,
        NativeTransactionJournalMutationEvent mutationEvent,
        uint systemStatus,
        uint systemError,
        CancellationToken cancellationToken)
    {
        var snapshot = await admission.ReadSnapshotAsync(cancellationToken);
        var observedAt = CurrentUtcMilliseconds(record.UpdatedAtUtcMilliseconds);
        var mutation = HostManagerTransactionJournalProjection.CreateMutation(
            in record,
            snapshot.Header.JournalRevision,
            mutationEvent,
            observedAt,
            systemStatus,
            systemError);
        RequireJournalStatus(
            await admission.MutateAsync(mutation, cancellationToken),
            mutationEvent.ToString());
        return await RequireJournalRecordAsync(admission, record.Identity, cancellationToken);
    }

    private async Task ApplyNonTerminalRecoveryEvidenceAsync(
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalRecord record,
        TransactionEffectResult effect,
        int retryDelayMilliseconds,
        CancellationToken cancellationToken)
    {
        var snapshot = await admission.ReadSnapshotAsync(cancellationToken);
        var observedAt = CurrentUtcMilliseconds(record.UpdatedAtUtcMilliseconds);
        var outcome = effect.Status == TransactionEffectStatus.OwnershipLost
            ? NativeTransactionJournalRecoveryOutcome.OwnershipLost
            : NativeTransactionJournalRecoveryOutcome.EffectInvocationUncertain;
        var evidence = HostManagerTransactionJournalProjection.CreateRecoveryEvidence(
            in record,
            snapshot.Header.JournalRevision,
            outcome,
            observedAt,
            0,
            effect.SystemStatus,
            effect.SystemError);
        RequireJournalStatus(
            await admission.ApplyRecoveryEvidenceAsync(evidence, cancellationToken),
            "non-terminal-recovery-evidence");
        _ = retryDelayMilliseconds;
    }

    private async Task<NativeTransactionJournalRecord> RequireJournalRecordAsync(
        HostManagerTransactionJournalAdmission admission,
        NativeTransactionJournalIdentity identity,
        CancellationToken cancellationToken)
    {
        var read = await admission.GetAsync(identity, cancellationToken);
        if (read.Status != NativeTransactionJournalStatus.Ok)
        {
            throw new InvalidDataException(
                $"Transaction-journal record read failed with {read.Status}.");
        }
        return read.Record;
    }

    private Task<TransactionPayloadCapture> CaptureTransactionPayloadAsync(
        NativeSmartCoordinatorAction action,
        HostManagerSample sample,
        long payloadByteBudget,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var capture = processPolicyTransaction.Capture(
            checked((int)action.ProcessId),
            HostManagerProcessPolicyTransactionFields.CpuPolicy);
        var status = ResolveProcessCaptureEffectStatus(capture.Status);
        if (status != TransactionEffectStatus.Succeeded)
        {
            return Task.FromResult(new TransactionPayloadCapture(status, [], 0));
        }
        if (checked((ulong)capture.ProcessStartKey) != action.ProcessStartKey)
        {
            return Task.FromResult(new TransactionPayloadCapture(
                TransactionEffectStatus.OwnershipLost, [], 0));
        }
        return Task.FromResult(new TransactionPayloadCapture(
            TransactionEffectStatus.Succeeded, capture.Payload, 0));
    }

    internal static TransactionEffectStatus ResolveProcessCaptureEffectStatus(
        HostManagerProcessPolicyCaptureStatus status) =>
        status switch
        {
            HostManagerProcessPolicyCaptureStatus.Captured =>
                TransactionEffectStatus.Succeeded,
            HostManagerProcessPolicyCaptureStatus.InvalidRequest =>
                TransactionEffectStatus.Rejected,
            HostManagerProcessPolicyCaptureStatus.Unavailable =>
                TransactionEffectStatus.Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private Task<TransactionEffectResult> ApplyTransactionPayloadAsync(
        NativeSmartCoordinatorAction action,
        HostManagerSample sample,
        byte[] payload,
        long payloadByteBudget,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
        => Task.FromResult(MapProcessApplyResult(
            processPolicyTransaction.Apply(payload, (int)action.ToProcessGrade)));

    private Task<TransactionEffectResult> ApplyPlannedTransitionPayloadAsync(
        NativeSmartCoordinatorAction action,
        HostManagerSample? sample,
        byte[] payload,
        NativeAppliedOwnershipCurrentGrades targetGrades,
        long payloadByteBudget,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        if (targetGrades.ValidMask == 0)
        {
            return Task.FromResult(TransactionEffectResult.Succeeded());
        }
        if (targetGrades.ValidMask != (uint)NativeAppliedOwnershipGradeValidity.Process)
        {
            throw new InvalidDataException(
                "The native ownership transition produced a non-canonical process target.");
        }
        return Task.FromResult(MapProcessApplyResult(
            processPolicyTransaction.Apply(payload, targetGrades.ProcessGrade)));
    }

    private Task<TransactionEffectResult> RestoreTransactionPayloadAsync(
        NativeSmartCoordinatorAction action,
        byte[] payload,
        long payloadByteBudget,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
        => Task.FromResult(MapProcessRestoreResult(
            processPolicyTransaction.Restore(payload, (int)action.FromProcessGrade)));

    internal static TransactionEffectResult MapProcessApplyResult(
        HostManagerProcessPolicyApplyResult result)
        => result.Status switch
        {
            HostManagerProcessPolicyApplyStatus.Applied or
                HostManagerProcessPolicyApplyStatus.AlreadyApplied =>
                TransactionEffectResult.Succeeded(),
            HostManagerProcessPolicyApplyStatus.Failed =>
                TransactionEffectResult.FailedUnchanged(),
            HostManagerProcessPolicyApplyStatus.InvalidPayload or
                HostManagerProcessPolicyApplyStatus.InvalidGrade or
                HostManagerProcessPolicyApplyStatus.InvalidTarget or
                HostManagerProcessPolicyApplyStatus.Level4Rejected =>
                TransactionEffectResult.Rejected(),
            HostManagerProcessPolicyApplyStatus.OwnershipLost =>
                TransactionEffectResult.OwnershipLost(),
            HostManagerProcessPolicyApplyStatus.ProcessExited =>
                TransactionEffectResult.ProcessExited(),
            HostManagerProcessPolicyApplyStatus.Unavailable or
                HostManagerProcessPolicyApplyStatus.StateUncertain =>
                TransactionEffectResult.StateUncertain(),
            _ => TransactionEffectResult.StateUncertain()
        };

    internal static TransactionEffectResult MapProcessRestoreResult(
        HostManagerProcessPolicyRestoreResult result)
        => result.Status switch
        {
            HostManagerProcessPolicyRestoreStatus.Restored or
                HostManagerProcessPolicyRestoreStatus.AlreadyRestored =>
                TransactionEffectResult.Succeeded(),
            HostManagerProcessPolicyRestoreStatus.Failed =>
                TransactionEffectResult.FailedUnchanged(),
            HostManagerProcessPolicyRestoreStatus.InvalidPayload or
                HostManagerProcessPolicyRestoreStatus.InvalidGrade or
                HostManagerProcessPolicyRestoreStatus.InvalidTarget or
                HostManagerProcessPolicyRestoreStatus.Level4Rejected =>
                TransactionEffectResult.Rejected(),
            HostManagerProcessPolicyRestoreStatus.OwnershipLost =>
                TransactionEffectResult.OwnershipLost(),
            HostManagerProcessPolicyRestoreStatus.ProcessExited =>
                TransactionEffectResult.ProcessExited(),
            HostManagerProcessPolicyRestoreStatus.Unavailable or
                HostManagerProcessPolicyRestoreStatus.StateUncertain =>
                TransactionEffectResult.StateUncertain(),
            _ => TransactionEffectResult.StateUncertain()
        };

    internal static NativeSmartCoordinatorFeedback CreateSucceededFeedback(
        in NativeSmartCoordinatorAction action,
        DateTimeOffset completedAt)
    {
        var flags = NativeSmartCoordinatorFeedbackFlags.None;
        if (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Process) &&
            action.ToProcessGrade != NativeSmartCoordinatorProcessGrade.Normal)
        {
            flags |= NativeSmartCoordinatorFeedbackFlags.ProcessOwned;
        }
        if (flags.HasFlag(NativeSmartCoordinatorFeedbackFlags.ProcessOwned))
        {
            flags |= NativeSmartCoordinatorFeedbackFlags.RollbackPayloadPersisted;
        }
        return CreateNativeFeedback(
            action,
            NativeSmartCoordinatorFeedbackStatus.Succeeded,
            completedAt,
            flags,
            actualProcessGrade: action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Process)
                ? action.ToProcessGrade
                : null);
    }

    internal static NativeSmartCoordinatorFeedback CreateUnchangedFeedback(
        in NativeSmartCoordinatorAction action,
        TransactionEffectStatus status,
        uint systemError,
        DateTimeOffset completedAt)
    {
        var feedbackStatus = status switch
        {
            TransactionEffectStatus.FailedUnchanged =>
                NativeSmartCoordinatorFeedbackStatus.FailedUnchanged,
            TransactionEffectStatus.Rejected =>
                NativeSmartCoordinatorFeedbackStatus.Rejected,
            TransactionEffectStatus.Skipped =>
                NativeSmartCoordinatorFeedbackStatus.Skipped,
            TransactionEffectStatus.OwnershipLost =>
                NativeSmartCoordinatorFeedbackStatus.OwnershipLost,
            TransactionEffectStatus.StateUncertain =>
                NativeSmartCoordinatorFeedbackStatus.StateUncertain,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
        var includeActual = feedbackStatus is NativeSmartCoordinatorFeedbackStatus.FailedUnchanged
            or NativeSmartCoordinatorFeedbackStatus.Rejected;
        return CreateNativeFeedback(
            action,
            feedbackStatus,
            completedAt,
            systemErrorCode: systemError,
            actualProcessGrade: includeActual &&
                action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Process)
                    ? action.FromProcessGrade
                    : null);
    }

    internal static NativeSmartCoordinatorFeedback CreateProcessExitedFeedback(
        in NativeSmartCoordinatorAction action,
        uint systemError,
        DateTimeOffset completedAt)
        => CreateUnchangedFeedback(
            in action,
            TransactionEffectStatus.OwnershipLost,
            systemError,
            completedAt);

    internal static TransactionEffectStatus ResolveTerminalFeedbackEffectStatus(
        TransactionEffectStatus effectStatus,
        bool previousOwnershipRestored)
    {
        if (effectStatus is not (TransactionEffectStatus.FailedUnchanged or
            TransactionEffectStatus.Rejected))
        {
            throw new ArgumentOutOfRangeException(nameof(effectStatus));
        }
        return previousOwnershipRestored
            ? TransactionEffectStatus.OwnershipLost
            : effectStatus;
    }

    private static NativeAppliedOwnershipPrimaryIdentity CreateAppliedOwnershipPrimary(
        in NativeSmartCoordinatorAction action)
        => action.Scope switch
        {
            NativeSmartCoordinatorActionScope.ProcessPolicy => new NativeAppliedOwnershipPrimaryIdentity
            {
                Scope = (uint)NativeAppliedOwnershipScope.Process,
                TargetId = action.TargetKey,
                SoftwareId = action.SoftwareKey,
                ProcessStartKey = action.ProcessStartKey,
                ProcessId = action.ProcessId
            },
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

    private static bool ActionFromGradeIsOwned(in NativeSmartCoordinatorAction action)
        => action.Scope switch
        {
            NativeSmartCoordinatorActionScope.ProcessPolicy =>
                action.FromProcessGrade != NativeSmartCoordinatorProcessGrade.Normal,
            _ => false
        };

    private ulong CurrentUtcMilliseconds(ulong minimumUtcMilliseconds = 0)
        => durableTimeSource.NextUtcMilliseconds(minimumUtcMilliseconds);

    private static ulong CheckedAdd(ulong value, ulong delta)
        => checked(value + delta);

    private async Task<bool> SkipPendingJournalTargetAsync(
        HostManagerTransactionJournalAdmission admission,
        NativeAppliedOwnershipPrimaryIdentity target,
        NativeTransactionJournalDomain domains,
        CancellationToken cancellationToken)
    {
        if (!await admission.HasPendingTargetAsync(target, domains, cancellationToken))
        {
            return false;
        }
        logger.LogWarning(
            "Skipping target with an unsettled journal record: scope {Scope}, process {ProcessId}/{ProcessStartKey}, software {SoftwareId}, domains {Domains}.",
            target.Scope, target.ProcessId, target.ProcessStartKey, target.SoftwareId, domains);
        return true;
    }

    private static void RequireJournalStatus(
        NativeTransactionJournalStatus status,
        string operation)
    {
        if (status != NativeTransactionJournalStatus.Ok)
        {
            throw new InvalidDataException(
                $"Transaction-journal {operation} failed with {status}.");
        }
    }

    private static void RequireOwnershipStatus(
        NativeAppliedOwnershipStatus status,
        string operation)
    {
        if (status != NativeAppliedOwnershipStatus.Ok)
        {
            throw new InvalidDataException(
                $"Applied ownership {operation} failed with {status}.");
        }
    }

    internal enum TransactionEffectStatus : byte
    {
        Succeeded = 1,
        FailedUnchanged = 2,
        Rejected = 3,
        OwnershipLost = 4,
        StateUncertain = 5,
        ProcessExited = 6,
        Skipped = 7
    }

    internal readonly record struct TransactionEffectResult(
        TransactionEffectStatus Status,
        uint SystemStatus,
        uint SystemError)
    {
        public static TransactionEffectResult Succeeded() =>
            new(TransactionEffectStatus.Succeeded, 0, 0);
        public static TransactionEffectResult FailedUnchanged() =>
            new(TransactionEffectStatus.FailedUnchanged, 0, 0);
        public static TransactionEffectResult Rejected() =>
            new(TransactionEffectStatus.Rejected, 0, 0);
        public static TransactionEffectResult OwnershipLost() =>
            new(TransactionEffectStatus.OwnershipLost, 0, 0);
        public static TransactionEffectResult StateUncertain() =>
            new(TransactionEffectStatus.StateUncertain, 0, 0);
        public static TransactionEffectResult ProcessExited() =>
            new(TransactionEffectStatus.ProcessExited, 0, 0);
    }

    private readonly record struct TransactionPayloadCapture(
        TransactionEffectStatus Status,
        byte[] Payload,
        uint SystemError);

    private readonly record struct NativeTransactionSettlement(
        NativeTransactionJournalIdentity Identity,
        bool DeletePayloadAfterAcknowledgement,
        NativeTransactionJournalPayloadReference PayloadReference,
        NativeTransactionJournalPayloadBinding PayloadBinding);

    private readonly record struct NativeTransactionExecutionBatch(
        uint FeedbackCount,
        uint SuccessfulFeedbackCount,
        IReadOnlyList<NativeTransactionSettlement> Settlements);

    private readonly record struct NativeTransactionActionResult(
        NativeSmartCoordinatorFeedback Feedback,
        NativeTransactionSettlement? Settlement)
    {
        public static NativeTransactionActionResult Direct(
            NativeSmartCoordinatorFeedback feedback) => new(feedback, null);
    }
}
