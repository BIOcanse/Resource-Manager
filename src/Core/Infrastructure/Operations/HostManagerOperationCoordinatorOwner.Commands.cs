using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

public sealed partial class HostManagerOperationCoordinatorOwner
{
    public async Task<HostManagerOperationSnapshot> SubmitAsync(
        HostManagerOperationSubmitCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMutableLocked();
            if (!HostManagerOperationKinds.All.Contains(command.Kind)
                || string.IsNullOrWhiteSpace(command.Title)
                || command.RequestSchemaId == 0
                || command.RequestSchemaVersion == 0
                || command.CanonicalRequest is null)
            {
                throw new ArgumentException(
                    "The operation submission command is non-canonical.",
                    nameof(command));
            }
            var policy = appliedPlan!.HotPublish.Kinds.Single(
                kind => string.Equals(
                    kind.Name,
                    command.Kind,
                    StringComparison.Ordinal));
            var operationId =
                NativeOperationCoordinatorWorkspace.CreateRandomHandle();
            var candidatePayloads = payloads.Clone();
            var kind = candidatePayloads.Add(
                HostManagerOperationRequestSchemas.KindName,
                HostManagerOperationRequestSchemas.Version,
                HostManagerOperationPayloadRole.Kind,
                workspace!.SessionInstanceId,
                operationId,
                default,
                HostManagerOperationRequestCodec.EncodeText(command.Kind));
            var title = candidatePayloads.Add(
                HostManagerOperationRequestSchemas.UserTitle,
                HostManagerOperationRequestSchemas.Version,
                HostManagerOperationPayloadRole.Title,
                workspace.SessionInstanceId,
                operationId,
                default,
                HostManagerOperationRequestCodec.EncodeText(command.Title));
            var request = candidatePayloads.Add(
                command.RequestSchemaId,
                command.RequestSchemaVersion,
                HostManagerOperationPayloadRole.Request,
                workspace.SessionInstanceId,
                operationId,
                default,
                command.CanonicalRequest);
            var domainHandle = default(NativeOperationHandle128);
            var validity =
                NativeOperationSubmitValidity.Title
                | NativeOperationSubmitValidity.Request;
            if (!string.IsNullOrWhiteSpace(command.DomainKey))
            {
                var domain = candidatePayloads.Add(
                    HostManagerOperationRequestSchemas.DomainKey,
                    HostManagerOperationRequestSchemas.Version,
                    HostManagerOperationPayloadRole.Domain,
                    workspace.SessionInstanceId,
                    default,
                    default,
                    HostManagerOperationRequestCodec.EncodeText(command.DomainKey));
                domainHandle = domain.Handle;
                validity |= NativeOperationSubmitValidity.Domain;
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var monotonic =
                NativeOperationCoordinatorWorkspace.MonotonicMilliseconds();
            var input = new NativeOperationSubmitInput
            {
                OperationId = operationId,
                DomainId = domainHandle,
                KindHandle = kind.Handle,
                TitleHandle = title.Handle,
                RequestHandle = request.Handle,
                Priority = policy.Priority,
                MaximumAttempts = policy.MaximumAttempts,
                RetryDelayMilliseconds = policy.RetryDelayMilliseconds,
                ExecutionTimeoutMilliseconds =
                    policy.ExecutionTimeoutMilliseconds,
                CancelGraceMilliseconds = policy.CancelGraceMilliseconds,
                TerminalRetentionMilliseconds =
                    policy.TerminalRetentionMilliseconds,
                CreatedUtcMilliseconds = now,
                CreatedMonotonicMilliseconds = monotonic,
                ObservedUtcMilliseconds = now,
                ObservedMonotonicMilliseconds = monotonic,
                ValidMask = (ulong)validity,
                Flags = 0
            };
            var output = workspace.SubmitLocked(ref input);
            var inserted = (output.Flags
                & (uint)NativeOperationSubmitOutputFlags.Inserted) != 0;
            CommitCurrentLocked(
                inserted ? "submit-publication" : "dedupe-publication",
                inserted ? candidatePayloads : payloads.Clone(),
                receipts.Clone());
            var id = FormatHandle(output.OperationId);
            var snapshot = Volatile.Read(ref committedState).ById.GetValueOrDefault(id)
                ?? throw new InvalidDataException(
                    "The submitted operation is absent from the committed projection.");
            SignalPlanner();
            return snapshot;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<HostManagerOperationSnapshot?> CancelAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (!TryParseHandle(operationId, out var handle))
        {
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMutableLocked();
            if (!Volatile.Read(ref committedState).ById.ContainsKey(operationId))
            {
                return null;
            }
            var input = new NativeOperationCancelInput
            {
                OperationId = handle,
                ObservedUtcMilliseconds =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ObservedMonotonicMilliseconds =
                    NativeOperationCoordinatorWorkspace.MonotonicMilliseconds(),
                Flags = 0
            };
            var output = workspace!.CancelLocked(ref input);
            CommitCurrentLocked("cancel-request-publication");
            var snapshot = Volatile.Read(ref committedState).ById
                .GetValueOrDefault(FormatHandle(output.OperationId))
                ?? throw new InvalidDataException(
                    "The canceled operation is absent from the committed projection.");
            SignalPlanner();
            return snapshot;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    async ValueTask IHostManagerOperationProgressSink.ReportAsync(
        HostManagerOperationActionTicket ticket,
        HostManagerOperationProgressUpdate progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMutableLocked();
            var operation = workspace!.ReadCommittedProjectionLocked()
                .SingleOrDefault(record =>
                    record.OperationId == ticket.Action.OperationId);
            if (operation.OperationId.IsZero
                || operation.AttemptToken != ticket.Action.AttemptToken)
            {
                return;
            }
            if (operation.ProgressSequence == ulong.MaxValue)
            {
                throw new InvalidOperationException(
                    "The operation progress sequence is exhausted.");
            }

            var candidatePayloads = payloads.Clone();
            var validity = NativeOperationProgressValidity.None;
            var input = new NativeOperationProgressInput
            {
                OperationId = ticket.Action.OperationId,
                AttemptToken = ticket.Action.AttemptToken,
                ProgressSequence = operation.ProgressSequence + 1,
                ObservedUtcMilliseconds =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ObservedMonotonicMilliseconds =
                    NativeOperationCoordinatorWorkspace.MonotonicMilliseconds()
            };
            if (progress.PercentMilli is not null)
            {
                input.PercentMilli = progress.PercentMilli.Value;
                validity |= NativeOperationProgressValidity.PercentMilli;
            }
            if (progress.BytesDone is not null)
            {
                input.BytesDone = progress.BytesDone.Value;
                validity |= NativeOperationProgressValidity.BytesDone;
            }
            if (progress.BytesTotal is not null)
            {
                input.BytesTotal = progress.BytesTotal.Value;
                validity |= NativeOperationProgressValidity.BytesTotal;
            }
            if (progress.SpeedBytesPerSecond is not null)
            {
                input.SpeedBytesPerSecond = progress.SpeedBytesPerSecond.Value;
                validity |= NativeOperationProgressValidity.SpeedBytesPerSecond;
            }
            if (progress.Stage is not null)
            {
                input.StageHandle = candidatePayloads.Add(
                    HostManagerOperationRequestSchemas.ProgressStage,
                    HostManagerOperationRequestSchemas.Version,
                    HostManagerOperationPayloadRole.ProgressStage,
                    workspace.SessionInstanceId,
                    ticket.Action.OperationId,
                    ticket.Action.AttemptToken,
                    HostManagerOperationRequestCodec.EncodeText(progress.Stage)).Handle;
                validity |= NativeOperationProgressValidity.Stage;
            }
            if (progress.Message is not null)
            {
                input.MessageHandle = candidatePayloads.Add(
                    HostManagerOperationRequestSchemas.ProgressMessage,
                    HostManagerOperationRequestSchemas.Version,
                    HostManagerOperationPayloadRole.ProgressMessage,
                    workspace.SessionInstanceId,
                    ticket.Action.OperationId,
                    ticket.Action.AttemptToken,
                    HostManagerOperationRequestCodec.EncodeText(progress.Message)).Handle;
                validity |= NativeOperationProgressValidity.Message;
            }
            if (progress.Checkpoint is not null)
            {
                input.CheckpointHandle = candidatePayloads.Add(
                    HostManagerOperationRequestSchemas.OperationCheckpoint,
                    HostManagerOperationRequestSchemas.Version,
                    HostManagerOperationPayloadRole.Checkpoint,
                    workspace.SessionInstanceId,
                    ticket.Action.OperationId,
                    ticket.Action.AttemptToken,
                    progress.Checkpoint).Handle;
                validity |= NativeOperationProgressValidity.Checkpoint;
            }
            input.ValidMask = (ulong)validity;
            if (!workspace.ReportProgressLocked(ref input))
            {
                return;
            }
            CommitCurrentLocked(
                "progress-publication",
                candidatePayloads,
                receipts.Clone());
        }
        finally
        {
            mutationGate.Release();
        }
    }
}
