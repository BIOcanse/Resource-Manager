using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

internal enum HostManagerTransactionJournalAdmissionKind
{
    Execution = 1,
    Recovery = 2
}

internal enum HostManagerTransactionJournalOperation
{
    Prepare = 1,
    PrepareBatch = 2,
    Mutate = 3,
    StageFeedback = 4,
    ApplyRecoveryEvidence = 5,
    Acknowledge = 6,
    Get = 7,
    ReadSnapshot = 8,
    PersistPayload = 9,
    ReadPayload = 10,
    DeletePayload = 11,
    ReconcilePayloads = 12
}

internal sealed class HostManagerTransactionJournalAdmission : IAsyncDisposable
{
    private readonly TaskCompletionSource disposalCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HostManagerTransactionJournalAdmissionIdentity identity;
    private readonly NativeTransactionJournalOwner journalOwner;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly NativeTransactionJournalPayloadStore payloadStore;
    private int closing;
    private HostManagerTransactionJournalRuntime? runtime;

    internal HostManagerTransactionJournalAdmission(
        HostManagerTransactionJournalRuntime runtime,
        NativeTransactionJournalOwner journalOwner,
        NativeTransactionJournalPayloadStore payloadStore,
        HostManagerTransactionJournalAdmissionIdentity identity)
    {
        if (!identity.IsValid)
        {
            throw new ArgumentException(
                "The transaction-journal admission identity is invalid.",
                nameof(identity));
        }
        this.runtime = runtime;
        this.journalOwner = journalOwner;
        this.payloadStore = payloadStore;
        this.identity = identity;
    }

    public HostManagerTransactionJournalAdmissionKind Kind => identity.Kind;

    public bool PayloadReconciliationRequired =>
        RequireRuntime().PayloadReconciliationRequired;

    public Task<NativeTransactionJournalStatus> PrepareAsync(
        NativeTransactionJournalPrepareInput input,
        CancellationToken cancellationToken)
        => ExecuteJournalOperationAsync(
            HostManagerTransactionJournalOperation.Prepare,
            cancellationToken,
            () => journalOwner.PrepareAsync(input, cancellationToken),
            runtime => runtime.ValidateExecutionConfigurationGeneration(
                input.Identity.ConfigurationGeneration,
                identity));

    public Task<NativeTransactionJournalStatus> PrepareBatchAsync(
        NativeTransactionJournalPrepareBatchInput batch,
        ReadOnlyMemory<NativeTransactionJournalPrepareInput> inputs,
        CancellationToken cancellationToken)
        => ExecuteJournalOperationAsync(
            HostManagerTransactionJournalOperation.PrepareBatch,
            cancellationToken,
            () => journalOwner.PrepareBatchAsync(batch, inputs, cancellationToken),
            runtime =>
            {
                foreach (var input in inputs.Span)
                {
                    runtime.ValidateExecutionConfigurationGeneration(
                        input.Identity.ConfigurationGeneration,
                        identity);
                }
            });

    public Task<NativeTransactionJournalStatus> MutateAsync(
        NativeTransactionJournalMutationInput input,
        CancellationToken cancellationToken)
        => ExecuteJournalOperationAsync(
            HostManagerTransactionJournalOperation.Mutate,
            cancellationToken,
            () => journalOwner.MutateAsync(input, cancellationToken));

    public Task<NativeTransactionJournalStatus> StageFeedbackAsync(
        NativeTransactionJournalStageFeedbackInput input,
        CancellationToken cancellationToken)
        => ExecuteJournalOperationAsync(
            HostManagerTransactionJournalOperation.StageFeedback,
            cancellationToken,
            () => journalOwner.StageFeedbackAsync(input, cancellationToken));

    public Task<NativeTransactionJournalStatus> ApplyRecoveryEvidenceAsync(
        NativeTransactionJournalRecoveryEvidenceInput input,
        CancellationToken cancellationToken)
        => ExecuteJournalOperationAsync(
            HostManagerTransactionJournalOperation.ApplyRecoveryEvidence,
            cancellationToken,
            () => journalOwner.ApplyRecoveryEvidenceAsync(input, cancellationToken));

    public Task<NativeTransactionJournalStatus> AcknowledgeAsync(
        NativeTransactionJournalAckInput input,
        CancellationToken cancellationToken)
        => ExecuteJournalOperationAsync(
            HostManagerTransactionJournalOperation.Acknowledge,
            cancellationToken,
            () => journalOwner.AcknowledgeAsync(input, cancellationToken));

    public Task<NativeTransactionJournalReadResult> GetAsync(
        NativeTransactionJournalIdentity identity,
        CancellationToken cancellationToken)
        => ExecuteJournalOperationAsync(
            HostManagerTransactionJournalOperation.Get,
            cancellationToken,
            () => journalOwner.GetAsync(identity, cancellationToken));

    public Task<NativeTransactionJournalSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken)
        => ExecuteJournalOperationAsync(
            HostManagerTransactionJournalOperation.ReadSnapshot,
            cancellationToken,
            () => journalOwner.ReadSnapshotAsync(cancellationToken),
            completed: static (runtime, snapshot) => runtime.ObserveSnapshot(snapshot));

    public Task<NativeTransactionJournalPayloadReference> PersistPayloadAsync(
        NativeTransactionJournalPayloadBinding binding,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
        => ExecutePayloadOperationAsync(
            HostManagerTransactionJournalOperation.PersistPayload,
            cancellationToken,
            () => payloadStore.PersistAsync(binding, payload, cancellationToken),
            runtime =>
            {
                runtime.RequirePayloadReconciliationComplete();
                runtime.ValidateExecutionConfigurationGeneration(
                    binding.ActionIdentity.ConfigurationGeneration,
                    identity);
            });

    public Task<byte[]> ReadPayloadAsync(
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadBinding expectedBinding,
        CancellationToken cancellationToken)
        => ExecutePayloadOperationAsync(
            HostManagerTransactionJournalOperation.ReadPayload,
            cancellationToken,
            () => payloadStore.ReadAsync(reference, expectedBinding, cancellationToken));

    public Task<byte[]> ReadPayloadAsync(
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadProvenance expectedProvenance,
        CancellationToken cancellationToken)
        => ExecutePayloadOperationAsync(
            HostManagerTransactionJournalOperation.ReadPayload,
            cancellationToken,
            () => payloadStore.ReadAsync(reference, expectedProvenance, cancellationToken));

    public Task<bool> DeletePayloadAsync(
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadBinding expectedBinding,
        CancellationToken cancellationToken)
        => ExecutePayloadOperationAsync(
            HostManagerTransactionJournalOperation.DeletePayload,
            cancellationToken,
            () => payloadStore.DeleteAsync(reference, expectedBinding, cancellationToken),
            completed: static (runtime, deleted) =>
            {
                if (!deleted)
                {
                    runtime.RequirePayloadReconciliation();
                }
            },
            failed: static (runtime, _) => runtime.RequirePayloadReconciliation());

    public Task<bool> DeletePayloadAsync(
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadProvenance expectedProvenance,
        CancellationToken cancellationToken)
        => ExecutePayloadOperationAsync(
            HostManagerTransactionJournalOperation.DeletePayload,
            cancellationToken,
            () => payloadStore.DeleteAsync(reference, expectedProvenance, cancellationToken),
            completed: static (runtime, deleted) =>
            {
                if (!deleted)
                {
                    runtime.RequirePayloadReconciliation();
                }
            },
            failed: static (runtime, _) => runtime.RequirePayloadReconciliation());

    public Task<NativeTransactionJournalPayloadReconciliationResult> ReconcilePayloadsAsync(
        ReadOnlyMemory<NativeTransactionJournalPayloadLiveReference> liveReferences,
        CancellationToken cancellationToken)
        => ExecutePayloadOperationAsync(
            HostManagerTransactionJournalOperation.ReconcilePayloads,
            cancellationToken,
            () => payloadStore.ReconcileAsync(liveReferences, cancellationToken),
            completed: static (runtime, _) => runtime.CompletePayloadReconciliation(),
            failed: static (runtime, _) => runtime.RequirePayloadReconciliation());

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref closing, 1, 0) != 0)
        {
            await disposalCompleted.Task;
            return;
        }

        try
        {
            await operationGate.WaitAsync(CancellationToken.None);
            try
            {
                var current = Interlocked.Exchange(ref runtime, null);
                current?.ReleaseAdmission(identity);
            }
            finally
            {
                operationGate.Release();
            }
            disposalCompleted.TrySetResult();
        }
        catch (Exception exception)
        {
            disposalCompleted.TrySetException(exception);
            throw;
        }
    }

    private async Task<T> ExecuteJournalOperationAsync<T>(
        HostManagerTransactionJournalOperation operationKind,
        CancellationToken cancellationToken,
        Func<Task<T>> operation,
        Action<HostManagerTransactionJournalRuntime>? validate = null,
        Action<HostManagerTransactionJournalRuntime, T>? completed = null)
    {
        ThrowIfClosing();
        EnsureOperationAllowed(operationKind);
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfClosing();
            var currentRuntime = RequireRuntime();
            await currentRuntime.EnterAdmissionOperationAsync(identity, cancellationToken);
            try
            {
                validate?.Invoke(currentRuntime);
                var result = await operation();
                completed?.Invoke(currentRuntime, result);
                return result;
            }
            finally
            {
                ReportJournalFaultIfPresent(currentRuntime);
                currentRuntime.ExitAdmissionOperation();
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<T> ExecutePayloadOperationAsync<T>(
        HostManagerTransactionJournalOperation operationKind,
        CancellationToken cancellationToken,
        Func<Task<T>> operation,
        Action<HostManagerTransactionJournalRuntime>? validate = null,
        Action<HostManagerTransactionJournalRuntime, T>? completed = null,
        Action<HostManagerTransactionJournalRuntime, Exception>? failed = null)
    {
        ThrowIfClosing();
        EnsureOperationAllowed(operationKind);
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfClosing();
            var currentRuntime = RequireRuntime();
            await currentRuntime.EnterAdmissionOperationAsync(identity, cancellationToken);
            try
            {
                validate?.Invoke(currentRuntime);
                try
                {
                    var result = await operation();
                    completed?.Invoke(currentRuntime, result);
                    return result;
                }
                catch (NativeTransactionJournalPayloadCapacityException exception)
                {
                    failed?.Invoke(currentRuntime, exception);
                    throw;
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    failed?.Invoke(currentRuntime, exception);
                    if (operationKind is not (
                        HostManagerTransactionJournalOperation.DeletePayload or
                        HostManagerTransactionJournalOperation.ReconcilePayloads))
                    {
                        currentRuntime.ReportPersistenceFailure(exception);
                    }
                    throw;
                }
                catch (Exception exception)
                {
                    failed?.Invoke(currentRuntime, exception);
                    throw;
                }
            }
            finally
            {
                currentRuntime.ExitAdmissionOperation();
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void EnsureOperationAllowed(
        HostManagerTransactionJournalOperation operationKind)
    {
        if (identity.Kind == HostManagerTransactionJournalAdmissionKind.Execution)
        {
            return;
        }
        if (identity.Kind == HostManagerTransactionJournalAdmissionKind.Recovery &&
            operationKind is HostManagerTransactionJournalOperation.ApplyRecoveryEvidence or
                HostManagerTransactionJournalOperation.Acknowledge or
                HostManagerTransactionJournalOperation.Get or
                HostManagerTransactionJournalOperation.ReadSnapshot or
                HostManagerTransactionJournalOperation.ReadPayload or
                HostManagerTransactionJournalOperation.DeletePayload or
                HostManagerTransactionJournalOperation.ReconcilePayloads)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The {identity.Kind} transaction-journal admission cannot execute {operationKind}.");
    }

    private void ReportJournalFaultIfPresent(
        HostManagerTransactionJournalRuntime currentRuntime)
    {
        if (journalOwner.State == NativeTransactionJournalOwnerState.PersistenceFaulted)
        {
            currentRuntime.ReportPersistenceFailure(
                journalOwner.PersistenceFailure ??
                new IOException("The native transaction journal durability barrier failed."));
        }
    }

    private HostManagerTransactionJournalRuntime RequireRuntime()
    {
        var current = Volatile.Read(ref runtime);
        ObjectDisposedException.ThrowIf(current is null, this);
        return current;
    }

    private void ThrowIfClosing()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref closing) != 0 || Volatile.Read(ref runtime) is null,
            this);
    }
}
