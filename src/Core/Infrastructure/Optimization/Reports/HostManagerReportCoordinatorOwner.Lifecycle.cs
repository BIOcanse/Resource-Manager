using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal sealed partial class HostManagerReportCoordinatorOwner
{
    public override void Dispose()
    {
        if (!executionGate.Wait(0))
            throw new InvalidOperationException("The report coordinator is in use; use DisposeAsync to drain it.");
        try
        {
            if (disposed) return;
            if (ExecuteTask is { IsCompleted: false } || workspaceHolds.HasReferences)
                throw new InvalidOperationException("The report coordinator is running; use DisposeAsync to drain it.");
            Interlocked.Exchange(ref retiring, 1);
            EndObservationRun();
            CloseResourcesLocked();
        }
        finally { executionGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref disposed)) return;
        // A blocked Persist must receive the original worker cancellation before
        // waiting for its execution gate. Already admitted direct work drains there.
        Interlocked.Exchange(ref retiring, 1);
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await executionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (ExecuteTask is { IsCompleted: false })
            {
                // An already admitted Start may have crossed its final check just
                // before retirement, then installed the base task after first Stop.
                // The gate has now drained that Start; cancel/join its actual task.
                executionGate.Release();
                try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { await executionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false); }
            }
        }
        finally { executionGate.Release(); }
        await workspaceHolds.WaitForDrainAsync().ConfigureAwait(false);
        await executionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { if (!disposed) CloseResourcesLocked(); }
        finally { executionGate.Release(); }
    }

    private void CloseResourcesLocked()
    {
        var failures = new List<Exception>();
        ReleaseObservationSubscriptions(failures);
        CloseWorkspacesLocked(failures);
        if (failures.Count != 0) ThrowFailures(failures);
        base.Dispose();
        disposed = true;
        // Keep these managed gates usable for a queued direct/publication entry.
        // Its retirement check rejects before any native access; no new Current.
    }

    private void RetireWorkspace(
        NativeReportCoordinatorWorkspace? candidate,
        ICollection<Exception> failures)
    {
        if (candidate is null) return;
        if (pendingWorkspaceClose is not null && !ReferenceEquals(pendingWorkspaceClose, candidate))
            throw new InvalidOperationException("A report workspace close obligation is already pending.");
        pendingWorkspaceClose = candidate;
        try
        {
            candidate.Dispose();
            pendingWorkspaceClose = null;
        }
        catch (Exception failure) { failures.Add(failure); }
    }

    private void ThrowIfRetiring()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref retiring) != 0 || disposed, this);

    private WorkspaceHold AcquireWorkspaceHold(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        workspaceHolds.AddReference(ThrowIfRetiring, () => planProvider.Published += OnPlanPublished);
        return new WorkspaceHold(this);
    }

    private async ValueTask ReleaseWorkspaceHoldAsync()
    {
        await executionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (workspaceHolds.IsLastReference) await FlushCountersBeforeCloseAsync().ConfigureAwait(false);
            workspaceHolds.Release(() =>
            {
                planProvider.Published -= OnPlanPublished;
                var failures = new List<Exception>();
                CloseWorkspacesLocked(failures);
                if (failures.Count != 0) ThrowFailures(failures);
            });
        }
        finally { executionGate.Release(); }
    }

    // A failed write is handled like a failed cycle write: it is logged and the
    // close goes on. Storage keeps the last committed rows.
    private async Task FlushCountersBeforeCloseAsync()
    {
        try { await FlushCountersAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception failure)
        {
            logger.LogError(
                failure,
                "Host Manager report counters could not be written before the workspace closed; storage keeps the last committed rows.");
        }
    }

    private void CloseWorkspacesLocked(ICollection<Exception> failures)
    {
        try
        {
            pendingWorkspaceClose?.Dispose();
            pendingWorkspaceClose = null;
        }
        catch (Exception failure) { failures.Add(failure); }
        try
        {
            workspace?.Dispose();
            workspace = null;
        }
        catch (Exception failure) { failures.Add(failure); }
    }

    // Admission precedes the execution gate, so a queued finite call retains the
    // same workspace as the operation it overlaps. References only own resources;
    // they never start the report block or change any control-tree facts.
    private sealed class WorkspaceHolder
    {
        private readonly object lifecycleGate = new();
        private int referenceCount;
        private TaskCompletionSource? drained;

        internal bool HasReferences
        {
            get { lock (lifecycleGate) return referenceCount != 0; }
        }

        // A reference added after this check keeps the workspace open; the
        // early write it caused is an ordinary counter checkpoint.
        internal bool IsLastReference
        {
            get { lock (lifecycleGate) return referenceCount == 1; }
        }

        internal void AddReference(Action checkAdmission, Action firstReference)
        {
            lock (lifecycleGate)
            {
                checkAdmission();
                if (referenceCount == 0)
                {
                    firstReference();
                    drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                referenceCount++;
            }
        }

        internal void Release(Action close)
        {
            lock (lifecycleGate)
            {
                if (--referenceCount != 0) return;
                try { close(); }
                finally { drained!.TrySetResult(); }
            }
        }

        internal Task WaitForDrainAsync()
        {
            lock (lifecycleGate) return drained?.Task ?? Task.CompletedTask;
        }
    }

    private sealed class WorkspaceHold(HostManagerReportCoordinatorOwner owner) : IAsyncDisposable
    {
        private HostManagerReportCoordinatorOwner? owner = owner;
        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref owner, null)?.ReleaseWorkspaceHoldAsync() ?? ValueTask.CompletedTask;
    }
}
