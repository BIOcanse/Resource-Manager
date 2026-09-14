using System.Runtime.ExceptionServices;

namespace ResourceManager.App.Infrastructure.Optimization;

internal enum HostManagerSmartCoordinatorLifecycleState
{
    Created = 0,
    Running = 1,
    Closing = 2,
    Closed = 3
}

internal enum HostManagerSmartCoordinatorTransitionPoint
{
    BeforeNativeCall = 1,
    AfterNativeCall = 2,
    BeforeReplacementCreate = 3,
    AfterReplacementCreate = 4,
    BeforeDeploymentSettlement = 5,
    AfterDeploymentSettlement = 6,
    BeforePointerCutover = 7,
    AfterPointerCutover = 8
}

internal enum HostManagerSmartCoordinatorShutdownPoint
{
    AdmissionClosed = 1,
    ExactWorkerJoined = 2,
    GateDrained = 3,
    OwnedResourcesDisposed = 4
}

internal interface IHostManagerSmartCoordinatorTransitionProbe
{
    void Reach(HostManagerSmartCoordinatorTransitionPoint point);
    void ObserveShutdown(HostManagerSmartCoordinatorShutdownPoint point);
}

public sealed partial class HostManagerSmartCoordinator
{
    private readonly object lifecycleSync = new();
    private Task? shutdownTask;
    private TaskCompletionSource? foregroundControlRequestsDrained;
    private IHostManagerSmartCoordinatorTransitionProbe? transitionProbe;
    private int lifecycleState;
    private int baseDisposeStarted;

    internal HostManagerSmartCoordinatorLifecycleState LifecycleState
        => (HostManagerSmartCoordinatorLifecycleState)Volatile.Read(ref lifecycleState);

    internal IHostManagerSmartCoordinatorTransitionProbe? TransitionProbe
    {
        set => Volatile.Write(ref transitionProbe, value);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lock (lifecycleSync)
        {
            var state = LifecycleState;
            if (state is HostManagerSmartCoordinatorLifecycleState.Closing or
                HostManagerSmartCoordinatorLifecycleState.Closed)
            {
                throw CreateLifecycleClosedException();
            }
            if (state != HostManagerSmartCoordinatorLifecycleState.Created)
            {
                throw new InvalidOperationException(
                    "The Host Manager smart coordinator has already been started.");
            }

            Volatile.Write(
                ref lifecycleState,
                (int)HostManagerSmartCoordinatorLifecycleState.Running);
            try
            {
                return base.StartAsync(cancellationToken);
            }
            catch
            {
                Volatile.Write(
                    ref lifecycleState,
                    (int)HostManagerSmartCoordinatorLifecycleState.Created);
                throw;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var shutdown = BeginShutdown();
        await shutdown.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        Exception? failure = null;
        try
        {
            BeginShutdown().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (Interlocked.Exchange(ref baseDisposeStarted, 1) == 0)
        {
            try
            {
                base.Dispose();
            }
            catch (Exception exception)
            {
                failure = CombineFailures(failure, exception);
            }
        }

        Rethrow(failure);
    }

    private Task BeginShutdown()
    {
        lock (lifecycleSync)
        {
            if (shutdownTask is not null)
            {
                return shutdownTask;
            }

            Volatile.Write(
                ref lifecycleState,
                (int)HostManagerSmartCoordinatorLifecycleState.Closing);
            ObserveShutdownPoint(HostManagerSmartCoordinatorShutdownPoint.AdmissionClosed);
            shutdownTask = ShutdownCoreAsync();
            return shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        Exception? failure = null;
        try
        {
            await base.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        ObserveShutdownPoint(HostManagerSmartCoordinatorShutdownPoint.ExactWorkerJoined);

        Task foregroundCompletion;
        lock (lifecycleSync)
            foregroundCompletion = foregroundControlRequestsDrained?.Task ?? Task.CompletedTask;
        // A foreground operation may need the gate again to finish its admitted cleanup.
        await foregroundCompletion.ConfigureAwait(false);

        var gateAcquired = false;
        try
        {
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            gateAcquired = true;
            ObserveShutdownPoint(HostManagerSmartCoordinatorShutdownPoint.GateDrained);
            await DrainGpuActionCheckpointAsync().ConfigureAwait(false);
            try { await DrainGpuCallbackPreparationAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = CombineFailures(failure, exception); }
            try { await DrainGpuWindowExecutionAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = CombineFailures(failure, exception); }
            try { await CloseOwnedGpuRemoteCallsAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = CombineFailures(failure, exception); }
            failure = CombineFailures(failure, DisposeOwnedResourcesCore());
            ObserveShutdownPoint(HostManagerSmartCoordinatorShutdownPoint.OwnedResourcesDisposed);
        }
        catch (Exception exception)
        {
            failure = CombineFailures(failure, exception);
        }
        finally
        {
            Volatile.Write(ref transitionProbe, null);
            Volatile.Write(
                ref lifecycleState,
                (int)HostManagerSmartCoordinatorLifecycleState.Closed);
            if (gateAcquired)
            {
                gate.Release();
            }
        }

        Rethrow(failure);
    }

    private Exception? DisposeOwnedResourcesCore()
    {
        Exception? failure = null;
        DisposeOwnedResource(ref memoryModeWorkspace, ref failure);
        DisposeOwnedResource(ref computeScoringWorkspace, ref failure);
        DisposeOwnedResource(ref nativeWorkspace, ref failure);
        DisposeOwnedResource(ref placementCoordinatorSession, ref failure);
        placementCoordinatorWorkspace = null;
        appliedNativeRuntimePlan = null;
        appliedPlacementCoordinatorPlan = null;
        return failure;
    }

    private static void DisposeOwnedResource<T>(ref T? resource, ref Exception? failure)
        where T : class, IDisposable
    {
        var current = resource;
        resource = null;
        if (current is null)
        {
            return;
        }

        try
        {
            current.Dispose();
        }
        catch (Exception exception)
        {
            failure = CombineFailures(failure, exception);
        }
    }

    private void RequireOperationAdmission()
    {
        if (LifecycleState is HostManagerSmartCoordinatorLifecycleState.Closing or
            HostManagerSmartCoordinatorLifecycleState.Closed)
        {
            throw CreateLifecycleClosedException();
        }
    }

    private void ReachTransitionPoint(HostManagerSmartCoordinatorTransitionPoint point)
        => Volatile.Read(ref transitionProbe)?.Reach(point);

    private void ObserveShutdownPoint(HostManagerSmartCoordinatorShutdownPoint point)
        => Volatile.Read(ref transitionProbe)?.ObserveShutdown(point);

    private static Exception? CombineFailures(Exception? current, Exception? next)
    {
        if (next is null)
        {
            return current;
        }
        return current is null ? next : new AggregateException(current, next);
    }

    private static void Rethrow(Exception? failure)
    {
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static ObjectDisposedException CreateLifecycleClosedException()
        => new(
            nameof(HostManagerSmartCoordinator),
            "The Host Manager smart coordinator is closing or closed.");
}
