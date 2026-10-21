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
            if (ExecuteTask is { IsCompleted: false })
                throw new InvalidOperationException("The report coordinator is running; use DisposeAsync to drain it.");
            Interlocked.Exchange(ref retiring, 1);
            UnsubscribeFromPlanProvider();
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
            if (!disposed) CloseResourcesLocked();
        }
        finally { executionGate.Release(); }
    }

    private void CloseResourcesLocked()
    {
        var failures = new List<Exception>();
        ReleaseObservationSubscriptions(failures);
        try
        {
            pendingWorkspaceClose?.Dispose();
            pendingWorkspaceClose = null;
            workspace?.Dispose();
            workspace = null;
        }
        catch (Exception failure) { failures.Add(failure); }
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
}
