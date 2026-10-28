using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

internal enum HostManagerTransactionJournalRuntimeState
{
    Initializing = 0,
    ReadyForExecution = 1,
    RecoveryRequired = 2,
    Faulted = 3,
    Draining = 4,
    Disposed = 5
}

internal enum HostManagerTransactionJournalShutdownResult
{
    Clean = 1,
    RecoveryRequired = 2,
    Faulted = 3,
    TimedOut = 4
}

internal readonly record struct HostManagerTransactionJournalAdmissionIdentity(
    ulong AdmissionId,
    HostManagerTransactionJournalAdmissionKind Kind,
    ulong ConfigurationGeneration)
{
    public bool IsValid =>
        AdmissionId != 0 &&
        Enum.IsDefined(Kind) &&
        ConfigurationGeneration != 0;
}

internal sealed partial class HostManagerTransactionJournalRuntime : IAsyncDisposable
{
    private readonly HostManagerTransactionJournalRuntimeConfiguration configuration;
    private readonly Func<ulong, IComputeScratch> createScratch;
    private readonly SemaphoreSlim shutdownGate = new(1, 1);
    private readonly SemaphoreSlim transactionOperationGate = new(1, 1);
    private readonly object sync = new();
    private HostManagerTransactionJournalAdmissionIdentity? activeAdmissionIdentity;
    private TaskCompletionSource? admissionsDrained;
    private Exception? failure;
    private NativeTransactionJournalOwner? journalOwner;
    private HostManagerTransactionJournalShutdownResult? lastShutdownResult;
    private NativeTransactionJournalSnapshot? lastSnapshot;
    private int ownedStatePending = -1;
    internal bool? OwnedStatePendingPublication => Volatile.Read(ref ownedStatePending) switch
    {
        < 0 => null,
        0 => false,
        _ => true
    };
    internal bool OwnedStatePending => OwnedStatePendingPublication == true;
    internal event Action? OwnedStateChanged;

    internal void PublishOwnedState(NativeTransactionJournalSnapshot snapshot)
    {
        var pending = snapshot.Records.Count != 0;
        if (Interlocked.Exchange(ref ownedStatePending, pending ? 1 : 0) != (pending ? 1 : 0))
            OwnedStateChanged?.Invoke();
    }
    private NativeTransactionJournalPayloadStore? payloadStore;
    private bool payloadReconciliationRequired = true;
    private ulong nextAdmissionId;
    private CompiledHostManagerTransactionJournalHotPublishPlan currentHotPublishPlan;
    private bool resumeAfterAbortedRecreate;
    private HostManagerTransactionJournalRuntimeState state =
        HostManagerTransactionJournalRuntimeState.Initializing;

    private HostManagerTransactionJournalRuntime(
        HostManagerTransactionJournalRuntimeConfiguration configuration,
        CompiledHostManagerTransactionJournalHotPublishPlan hotPublishPlan,
        Func<ulong, IComputeScratch> createScratch)
    {
        this.configuration = configuration;
        this.createScratch = createScratch;
        currentHotPublishPlan = hotPublishPlan;
    }

    public HostManagerTransactionJournalRuntimeState State
    {
        get
        {
            lock (sync)
            {
                ObserveJournalFaultLocked();
                return state;
            }
        }
    }

    public Exception? Failure
    {
        get
        {
            lock (sync)
            {
                ObserveJournalFaultLocked();
                return failure;
            }
        }
    }

    public NativeTransactionJournalSnapshot? StartupSnapshot { get; private set; }

    public NativeTransactionJournalSnapshot? LastSnapshot
    {
        get
        {
            lock (sync)
            {
                return lastSnapshot;
            }
        }
    }

    public HostManagerTransactionJournalShutdownResult? LastShutdownResult
    {
        get
        {
            lock (sync)
            {
                return lastShutdownResult;
            }
        }
    }

    public string DataRoot => configuration.DataRoot;

    public string JournalPath => configuration.JournalPath;

    public string PayloadDirectory => configuration.PayloadDirectory;

    public uint RecordCapacity => configuration.RecordCapacity;

    public ulong ResidentByteBudget => configuration.ResidentByteBudget;

    public int PayloadCount => configuration.PayloadCount;

    public long PayloadByteBudget => configuration.PayloadByteBudget;

    public ulong ConfigurationGeneration
    {
        get
        {
            lock (sync)
            {
                return currentHotPublishPlan.ConfigurationGeneration;
            }
        }
    }

    public int MaximumRecoveryAttempts
    {
        get
        {
            lock (sync)
            {
                return currentHotPublishPlan.MaximumRecoveryAttempts;
            }
        }
    }

    public TimeSpan RetryDelay
    {
        get
        {
            lock (sync)
            {
                return TimeSpan.FromMilliseconds(currentHotPublishPlan.RetryDelayMilliseconds);
            }
        }
    }

    public TimeSpan RecoveryDeadline
    {
        get
        {
            lock (sync)
            {
                return TimeSpan.FromMilliseconds(
                    currentHotPublishPlan.RecoveryDeadlineMilliseconds);
            }
        }
    }

    public TimeSpan MaximumFutureSkew
    {
        get
        {
            lock (sync)
            {
                return TimeSpan.FromMilliseconds(
                    currentHotPublishPlan.MaximumFutureSkewMilliseconds);
            }
        }
    }

    public TimeSpan ShutdownDrainTimeout
    {
        get
        {
            lock (sync)
            {
                return TimeSpan.FromMilliseconds(
                    currentHotPublishPlan.ShutdownDrainTimeoutMilliseconds);
            }
        }
    }

    public int ActiveAdmissionCount
    {
        get
        {
            lock (sync)
            {
                return activeAdmissionIdentity.HasValue ? 1 : 0;
            }
        }
    }

    internal bool PayloadReconciliationRequired
    {
        get
        {
            lock (sync)
            {
                return payloadReconciliationRequired;
            }
        }
    }

    public bool TryAcquireExecutionAdmission(
        out HostManagerTransactionJournalAdmission? admission)
        => TryAcquireTypedAdmission(
            HostManagerTransactionJournalAdmissionKind.Execution,
            HostManagerTransactionJournalRuntimeState.ReadyForExecution,
            out admission);

    public bool TryAcquireRecoveryAdmission(
        out HostManagerTransactionJournalAdmission? admission)
        => TryAcquireTypedAdmission(
            HostManagerTransactionJournalAdmissionKind.Recovery,
            HostManagerTransactionJournalRuntimeState.RecoveryRequired,
            out admission);

    public async Task<bool> TryApplyHotPublishAsync(
        CompiledHostManagerTransactionJournalHotPublishPlan hotPublishPlan,
        CancellationToken cancellationToken)
    {
        ValidateHotPublishPlan(hotPublishPlan, nameof(hotPublishPlan));
        await transactionOperationGate.WaitAsync(cancellationToken);
        try
        {
            lock (sync)
            {
                ObserveJournalFaultLocked();
                if (state is not (HostManagerTransactionJournalRuntimeState.ReadyForExecution or
                    HostManagerTransactionJournalRuntimeState.RecoveryRequired))
                {
                    return false;
                }

                var currentGeneration = currentHotPublishPlan.ConfigurationGeneration;
                if (activeAdmissionIdentity.HasValue)
                {
                    return hotPublishPlan.ConfigurationGeneration == currentGeneration &&
                        hotPublishPlan == currentHotPublishPlan;
                }
                if (hotPublishPlan.ConfigurationGeneration < currentGeneration)
                {
                    return false;
                }
                if (hotPublishPlan.ConfigurationGeneration == currentGeneration)
                {
                    return hotPublishPlan == currentHotPublishPlan;
                }

                currentHotPublishPlan = hotPublishPlan;
                return true;
            }
        }
        finally
        {
            transactionOperationGate.Release();
        }
    }

    private bool TryAcquireTypedAdmission(
        HostManagerTransactionJournalAdmissionKind kind,
        HostManagerTransactionJournalRuntimeState requiredState,
        out HostManagerTransactionJournalAdmission? admission)
    {
        lock (sync)
        {
            ObserveJournalFaultLocked();
            if ((state != requiredState &&
                    !(kind == HostManagerTransactionJournalAdmissionKind.Execution &&
                        state == HostManagerTransactionJournalRuntimeState.RecoveryRequired)) ||
                journalOwner is null ||
                payloadStore is null ||
                activeAdmissionIdentity.HasValue)
            {
                admission = null;
                return false;
            }

            nextAdmissionId = checked(nextAdmissionId + 1);
            var identity = new HostManagerTransactionJournalAdmissionIdentity(
                nextAdmissionId,
                kind,
                currentHotPublishPlan.ConfigurationGeneration);
            if (!identity.IsValid)
            {
                throw new InvalidOperationException(
                    "The transaction-journal admission identity is invalid.");
            }
            var acquired = new HostManagerTransactionJournalAdmission(
                this,
                journalOwner,
                payloadStore,
                identity);
            activeAdmissionIdentity = identity;
            admission = acquired;
            return true;
        }
    }

    public async Task<HostManagerTransactionJournalShutdownResult> ShutdownAsync(
        CancellationToken cancellationToken)
    {
        await shutdownGate.WaitAsync(cancellationToken);
        try
        {
            Task drainTask;
            TimeSpan shutdownDrainTimeout;
            lock (sync)
            {
                ObserveJournalFaultLocked();
                if (state == HostManagerTransactionJournalRuntimeState.Disposed)
                {
                    return lastShutdownResult ??
                        HostManagerTransactionJournalShutdownResult.Faulted;
                }
                if (state == HostManagerTransactionJournalRuntimeState.Initializing)
                {
                    throw new InvalidOperationException(
                        "The transaction-journal runtime is still initializing.");
                }

                state = HostManagerTransactionJournalRuntimeState.Draining;
                resumeAfterAbortedRecreate = false;
                shutdownDrainTimeout = TimeSpan.FromMilliseconds(
                    currentHotPublishPlan.ShutdownDrainTimeoutMilliseconds);
                if (!activeAdmissionIdentity.HasValue)
                {
                    drainTask = Task.CompletedTask;
                }
                else
                {
                    admissionsDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    drainTask = admissionsDrained.Task;
                }
            }

            try
            {
                await drainTask.WaitAsync(shutdownDrainTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                lock (sync)
                {
                    lastShutdownResult = HostManagerTransactionJournalShutdownResult.TimedOut;
                }
                return HostManagerTransactionJournalShutdownResult.TimedOut;
            }

            var result = await ClassifyDrainedStateAsync();
            var disposeFailure = await DisposeOwnedResourcesAsync();
            if (disposeFailure is not null)
            {
                lock (sync)
                {
                    RecordFailureLocked(disposeFailure);
                }
                result = HostManagerTransactionJournalShutdownResult.Faulted;
            }

            lock (sync)
            {
                state = disposeFailure is null
                    ? HostManagerTransactionJournalRuntimeState.Disposed
                    : HostManagerTransactionJournalRuntimeState.Faulted;
                lastShutdownResult = result;
            }
            return result;
        }
        finally
        {
            shutdownGate.Release();
        }
    }

    internal async Task<HostManagerTransactionJournalShutdownResult> QuiesceForRecreateAsync(
        CancellationToken cancellationToken)
    {
        await shutdownGate.WaitAsync(cancellationToken);
        try
        {
            Task drainTask;
            TimeSpan drainTimeout;
            lock (sync)
            {
                ObserveJournalFaultLocked();
                if (state is HostManagerTransactionJournalRuntimeState.Initializing or
                    HostManagerTransactionJournalRuntimeState.Draining or
                    HostManagerTransactionJournalRuntimeState.Disposed)
                {
                    throw new InvalidOperationException(
                        $"The transaction journal cannot enter recreate quiescence from {state}.");
                }
                if (state == HostManagerTransactionJournalRuntimeState.Faulted)
                {
                    return HostManagerTransactionJournalShutdownResult.Faulted;
                }

                state = HostManagerTransactionJournalRuntimeState.Draining;
                resumeAfterAbortedRecreate = false;
                drainTimeout = TimeSpan.FromMilliseconds(
                    currentHotPublishPlan.ShutdownDrainTimeoutMilliseconds);
                if (!activeAdmissionIdentity.HasValue)
                {
                    drainTask = Task.CompletedTask;
                }
                else
                {
                    admissionsDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    drainTask = admissionsDrained.Task;
                }
            }

            try
            {
                await drainTask.WaitAsync(drainTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return HostManagerTransactionJournalShutdownResult.TimedOut;
            }
            return await ClassifyDrainedStateAsync();
        }
        finally
        {
            shutdownGate.Release();
        }
    }

    internal void ResumeAfterAbortedRecreate()
    {
        lock (sync)
        {
            if (state != HostManagerTransactionJournalRuntimeState.Draining)
            {
                throw new InvalidOperationException(
                    $"The transaction journal cannot resume an aborted recreate from {state}.");
            }
            if (activeAdmissionIdentity.HasValue)
            {
                resumeAfterAbortedRecreate = true;
                return;
            }
            ResumeAfterAbortedRecreateLocked();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var result = await ShutdownAsync(CancellationToken.None);
        if (result == HostManagerTransactionJournalShutdownResult.TimedOut)
        {
            throw new TimeoutException(
                "Transaction-journal runtime shutdown timed out with active admissions.");
        }
        if (State != HostManagerTransactionJournalRuntimeState.Disposed)
        {
            throw new InvalidOperationException(
                "The transaction-journal runtime still owns resources awaiting close.", Failure);
        }
    }

    internal void ReportPersistenceFailure(Exception exception)
    {
        lock (sync)
        {
            RecordFailureLocked(exception);
            if (state is HostManagerTransactionJournalRuntimeState.ReadyForExecution or
                HostManagerTransactionJournalRuntimeState.RecoveryRequired)
            {
                state = HostManagerTransactionJournalRuntimeState.Faulted;
            }
        }
    }

    internal void RequirePayloadReconciliation()
    {
        lock (sync)
        {
            payloadReconciliationRequired = true;
        }
    }

    internal void CompletePayloadReconciliation()
    {
        lock (sync)
        {
            payloadReconciliationRequired = false;
        }
    }

    internal void RequirePayloadReconciliationComplete()
    {
        lock (sync)
        {
            if (payloadReconciliationRequired)
            {
                throw new InvalidOperationException(
                    "Rollback payloads must be reconciled before a new payload is persisted.");
            }
        }
    }

    internal async Task EnterAdmissionOperationAsync(
        HostManagerTransactionJournalAdmissionIdentity identity,
        CancellationToken cancellationToken)
    {
        await transactionOperationGate.WaitAsync(cancellationToken);
        try
        {
            lock (sync)
            {
                ObserveJournalFaultLocked();
                if (state is HostManagerTransactionJournalRuntimeState.Faulted or
                    HostManagerTransactionJournalRuntimeState.Disposed or
                    HostManagerTransactionJournalRuntimeState.Initializing)
                {
                    throw new InvalidOperationException(
                        $"The transaction-journal admission is not valid while the runtime is {state}.");
                }
                if (!identity.IsValid ||
                    activeAdmissionIdentity is not { } active ||
                    active != identity)
                {
                    throw new InvalidOperationException(
                        "The transaction-journal admission identity is stale.");
                }
            }
        }
        catch
        {
            transactionOperationGate.Release();
            throw;
        }
    }

    internal void ExitAdmissionOperation()
        => transactionOperationGate.Release();

    internal void ObserveSnapshot(NativeTransactionJournalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        PublishOwnedState(snapshot);
        lock (sync)
        {
            lastSnapshot = snapshot;
            if (activeAdmissionIdentity.HasValue)
            {
                return;
            }
            if (state is HostManagerTransactionJournalRuntimeState.ReadyForExecution or
                HostManagerTransactionJournalRuntimeState.RecoveryRequired)
            {
                state = snapshot.Records.Count == 0
                    ? HostManagerTransactionJournalRuntimeState.ReadyForExecution
                    : HostManagerTransactionJournalRuntimeState.RecoveryRequired;
            }
        }
    }

    internal void ReleaseAdmission(HostManagerTransactionJournalAdmissionIdentity identity)
    {
        TaskCompletionSource? drained = null;
        lock (sync)
        {
            if (!identity.IsValid ||
                activeAdmissionIdentity is not { } active ||
                active != identity)
            {
                throw new InvalidOperationException(
                    "The transaction-journal admission release identity is stale.");
            }

            activeAdmissionIdentity = null;
            ObserveJournalFaultLocked();
            if (state is HostManagerTransactionJournalRuntimeState.ReadyForExecution or
                HostManagerTransactionJournalRuntimeState.RecoveryRequired)
            {
                state = lastSnapshot?.Records.Count > 0
                    ? HostManagerTransactionJournalRuntimeState.RecoveryRequired
                    : HostManagerTransactionJournalRuntimeState.ReadyForExecution;
            }
            if (state == HostManagerTransactionJournalRuntimeState.Draining)
            {
                drained = admissionsDrained;
                if (resumeAfterAbortedRecreate)
                {
                    ResumeAfterAbortedRecreateLocked();
                }
            }
        }
        drained?.TrySetResult();
    }

    internal async Task<NativeTransactionJournalSnapshot> ReadSnapshotForLifecycleAsync(
        CancellationToken cancellationToken)
    {
        await transactionOperationGate.WaitAsync(cancellationToken);
        try
        {
            NativeTransactionJournalOwner owner;
            lock (sync)
            {
                ObserveJournalFaultLocked();
                if (activeAdmissionIdentity.HasValue)
                {
                    throw new InvalidOperationException(
                        "The transaction-journal lifecycle snapshot cannot overlap an active admission.");
                }
                if (state is HostManagerTransactionJournalRuntimeState.Faulted or
                    HostManagerTransactionJournalRuntimeState.Draining or
                    HostManagerTransactionJournalRuntimeState.Disposed or
                    HostManagerTransactionJournalRuntimeState.Initializing)
                {
                    throw new InvalidOperationException(
                        $"The transaction-journal lifecycle snapshot is unavailable from {state}.");
                }
                owner = journalOwner
                    ?? throw new InvalidOperationException(
                        "The transaction-journal owner is unavailable.");
            }

            var snapshot = await owner.ReadSnapshotAsync(cancellationToken);
            ObserveSnapshot(snapshot);
            return snapshot;
        }
        finally
        {
            transactionOperationGate.Release();
        }
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var openedOwner = await NativeTransactionJournalOwner.OpenOrCreateAsync(
                new NativeTransactionJournalFileStore(configuration.JournalPath),
                configuration.CreateNativeConfiguration(),
                configuration.CreateNativeOpenConfiguration(),
                createScratch,
                cancellationToken);
            lock (sync) { journalOwner = openedOwner; }
            var openedPayloadStore = new NativeTransactionJournalPayloadStore(
                configuration.PayloadDirectory,
                configuration.PayloadCount,
                configuration.PayloadByteBudget);
            lock (sync) { payloadStore = openedPayloadStore; }
            var snapshot = await openedOwner.ReadSnapshotAsync(cancellationToken);
            PublishOwnedState(snapshot);
            lock (sync)
            {
                StartupSnapshot = snapshot;
                lastSnapshot = snapshot;
                state = snapshot.Records.Count == 0
                    ? HostManagerTransactionJournalRuntimeState.ReadyForExecution
                    : HostManagerTransactionJournalRuntimeState.RecoveryRequired;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cleanupFailure = await DisposeOwnedResourcesAsync();
            lock (sync)
            {
                if (cleanupFailure is not null) RecordFailureLocked(cleanupFailure);
                state = cleanupFailure is null
                    ? HostManagerTransactionJournalRuntimeState.Disposed
                    : HostManagerTransactionJournalRuntimeState.Faulted;
            }
            if (cleanupFailure is not null)
                throw new InvalidOperationException("Canceled journal initialization retains resources awaiting close.", cleanupFailure);
            throw;
        }
        catch (Exception exception)
        {
            var cleanupFailure = await DisposeOwnedResourcesAsync();
            var initializationFailure = cleanupFailure is null
                ? exception
                : new AggregateException(exception, cleanupFailure);
            lock (sync)
            {
                RecordFailureLocked(initializationFailure);
                state = HostManagerTransactionJournalRuntimeState.Faulted;
            }
            throw new InvalidOperationException(
                "The transaction-journal runtime failed to initialize.", initializationFailure);
        }
    }

    private async Task<HostManagerTransactionJournalShutdownResult> ClassifyDrainedStateAsync()
    {
        NativeTransactionJournalOwner? owner;
        lock (sync)
        {
            ObserveJournalFaultLocked();
            owner = journalOwner;
            if (failure is not null)
            {
                return HostManagerTransactionJournalShutdownResult.Faulted;
            }
        }

        if (owner is null)
        {
            return HostManagerTransactionJournalShutdownResult.Faulted;
        }

        try
        {
            var snapshot = await owner.ReadSnapshotAsync(CancellationToken.None);
            lock (sync)
            {
                lastSnapshot = snapshot;
            }
            return snapshot.Records.Count == 0
                ? HostManagerTransactionJournalShutdownResult.Clean
                : HostManagerTransactionJournalShutdownResult.RecoveryRequired;
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                RecordFailureLocked(exception);
            }
            return HostManagerTransactionJournalShutdownResult.Faulted;
        }
    }

    private async Task<Exception?> DisposeOwnedResourcesAsync()
    {
        NativeTransactionJournalOwner? owner;
        NativeTransactionJournalPayloadStore? store;
        lock (sync) { owner = journalOwner; store = payloadStore; }
        try
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
                lock (sync) { journalOwner = null; }
            }
            if (store is not null)
            {
                store.Dispose();
                lock (sync) { payloadStore = null; }
            }
            return null;
        }
        catch (Exception exception)
        {
            return exception; // Keep every not-yet-closed owner/store reachable for explicit retry.
        }
    }

    private void ObserveJournalFaultLocked()
    {
        if (journalOwner?.State != NativeTransactionJournalOwnerState.PersistenceFaulted)
        {
            return;
        }

        RecordFailureLocked(
            journalOwner.PersistenceFailure ??
            new IOException("The native transaction journal durability barrier failed."));
        if (state is HostManagerTransactionJournalRuntimeState.ReadyForExecution or
            HostManagerTransactionJournalRuntimeState.RecoveryRequired)
        {
            state = HostManagerTransactionJournalRuntimeState.Faulted;
        }
    }

    private void ResumeAfterAbortedRecreateLocked()
    {
        resumeAfterAbortedRecreate = false;
        state = failure is not null
            ? HostManagerTransactionJournalRuntimeState.Faulted
            : lastSnapshot?.Records.Count > 0
                ? HostManagerTransactionJournalRuntimeState.RecoveryRequired
                : HostManagerTransactionJournalRuntimeState.ReadyForExecution;
    }

    private void RecordFailureLocked(Exception exception)
    {
        failure = failure is null
            ? exception
            : ReferenceEquals(failure, exception)
                ? failure
                : new AggregateException(failure, exception);
    }
}
