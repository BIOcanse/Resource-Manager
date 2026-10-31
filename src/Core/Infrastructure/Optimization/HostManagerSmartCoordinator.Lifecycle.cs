using System.Runtime.ExceptionServices;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

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
    void ObserveNativeInputs(ReadOnlySpan<NativeSmartCoordinatorInputRow> inputs) { }
    void ObservePlacementPlan(ReadOnlySpan<NativePlacementDesiredInput> desired,
        ReadOnlySpan<NativePlacementAppliedInput> applied, ReadOnlySpan<NativePlacementAction> actions) { }
    void ObservePlacementFeedback(ReadOnlySpan<NativePlacementFeedback> feedback) { }
}

public sealed partial class HostManagerSmartCoordinator
{
    private readonly object lifecycleSync = new();
    private Task? shutdownTask;
    private TaskCompletionSource? foregroundControlRequestsDrained;
    private IHostManagerSmartCoordinatorTransitionProbe? transitionProbe;
    private int lifecycleState;
    private int baseDisposeStarted;
    private RuntimePlanProvider? runtimePlanNotifications;

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
            if (Volatile.Read(ref baseDisposeStarted) != 0 ||
                state == HostManagerSmartCoordinatorLifecycleState.Closing)
            {
                throw CreateLifecycleClosedException();
            }
            if (state == HostManagerSmartCoordinatorLifecycleState.Running)
            {
                throw new InvalidOperationException(
                    "The Host Manager smart coordinator has already been started.");
            }

            // A completed hosted stop releases this run's native resources. The
            // same singleton may start another run until DI finally disposes it.
            if (state == HostManagerSmartCoordinatorLifecycleState.Closed)
            {
                shutdownTask = null;
                schedulingWakeDeadline.PublishScheduled(null);
            }

            Volatile.Write(
                ref lifecycleState,
                (int)HostManagerSmartCoordinatorLifecycleState.Running);
            try
            {
                runtimePlanNotifications = runtimePlanProvider as RuntimePlanProvider;
                if (runtimePlanNotifications is not null)
                    runtimePlanNotifications.Published += OnRuntimePlanPublished;
                return base.StartAsync(cancellationToken);
            }
            catch
            {
                UnsubscribeActivationSignals();
                Volatile.Write(
                    ref lifecycleState,
                    (int)state);
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
        // A blocked normal close keeps the original owner and base resources valid.
        BeginShutdown().GetAwaiter().GetResult();
        if (Interlocked.Exchange(ref baseDisposeStarted, 1) == 0)
        {
            base.Dispose();
        }
    }

    private Task BeginShutdown()
    {
        lock (lifecycleSync)
        {
            if (shutdownTask is not null && !shutdownTask.IsFaulted && !shutdownTask.IsCanceled)
            {
                return shutdownTask;
            }
            // Only a new explicit close retries a completed failed attempt.
            // Concurrent callers still share the original in-progress task.

            Volatile.Write(
                ref lifecycleState,
                (int)HostManagerSmartCoordinatorLifecycleState.Closing);
            UnsubscribeActivationSignals();
            ObserveShutdownPoint(HostManagerSmartCoordinatorShutdownPoint.AdmissionClosed);
            shutdownTask = ShutdownCoreAsync();
            return shutdownTask;
        }
    }

    private void OnRuntimePlanPublished(CompiledRuntimePlan _)
        => WakeFromRunningPublication();

    private void WakeFromRunningPublication()
    {
        lock (lifecycleSync)
        {
            if (LifecycleState != HostManagerSmartCoordinatorLifecycleState.Running) return;
            schedulingWakeDeadline.PublishForeground(TimeSpan.Zero);
        }
    }

    private void UnsubscribeActivationSignals()
    {
        if (runtimePlanNotifications is not null)
        {
            runtimePlanNotifications.Published -= OnRuntimePlanPublished;
            runtimePlanNotifications = null;
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
            await RestoreTemporaryEffectsOnShutdownAsync().ConfigureAwait(false);
            failure = CombineFailures(failure, DisposeOwnedResourcesCore());
            if (failure is null)
                ObserveShutdownPoint(HostManagerSmartCoordinatorShutdownPoint.OwnedResourcesDisposed);
        }
        catch (Exception exception)
        {
            failure = CombineFailures(failure, exception);
        }
        finally
        {
            if (failure is null)
            {
                Volatile.Write(ref transitionProbe, null);
                Volatile.Write(
                    ref lifecycleState,
                    (int)HostManagerSmartCoordinatorLifecycleState.Closed);
            }
            if (gateAcquired)
            {
                gate.Release();
            }
        }

        Rethrow(failure);
    }

    private async Task RestoreTemporaryEffectsOnShutdownAsync()
    {
        // The worker and admitted requests have joined and this method owns the gate.
        // Restore through the same receipts and transaction journal as Normal mode.
        var restoration = OptimizationSettings.ProductDefaults.NormalRestoration;
        using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(restoration.TimeoutMilliseconds));
        try
        {
            var validation = processEffectValidationScopeAuthorityOwner.Capture();
            var effects = HostManagerCycleEffectAdmission.CreateForValidation(
                runtimePlanProvider.Current.Diagnostics.HostManagerSmartCoordinatorScoreOnlyEnabled, validation);
            if (effects.IsScoreOnly) return;
            effects.InitializeBudgets(restoration.MaximumActions, restoration.MaximumActions);
            var recovery = await RecoverNativeTransactionsAsync(effects, limit.Token);
            if (recovery.IsBlocked || !nativeActionTransactions.TryAcquireExecutionAdmission(out var admission) || admission is null)
            {
                logger.LogWarning("Temporary scheduling restoration remains pending at shutdown.");
                return;
            }
            await using var owner = admission;
            var restored = await RestoreOwnedEffectsForNormalModeAsync(effects, validation, admission,
                restoration.MaximumActions, limit.Token, gpuControlAvailable: !runningGpuPlacementActions.HasUnreleasedExternalControl,
                forShutdown: true);
            if (!restored) logger.LogWarning("Some temporary scheduling receipts remain for the next restoration attempt.");
        }
        catch (Exception exception)
        {
            // Do not erase failed ownership or hold service shutdown open indefinitely.
            logger.LogWarning(exception, "Temporary scheduling restoration did not complete; persisted receipts are retained.");
        }
    }

    private Exception? DisposeOwnedResourcesCore()
    {
        Exception? failure = null;
        DisposeOwnedResource(ref memoryModeWorkspace, ref failure);
        DisposeOwnedResource(ref computeScoringWorkspace, ref failure);
        DisposeOwnedResource(ref pendingNativeWorkspaceClose, ref failure);
        DisposeOwnedResource(ref nativeWorkspace, ref failure);
        try { ClosePendingPlacementCoordinator(); }
        catch (Exception exception) { failure = CombineFailures(failure, exception); }
        DisposeOwnedResource(ref placementCoordinatorWorkspace, ref failure);
        if (placementCoordinatorWorkspace is null)
        {
            DisposeOwnedResource(ref placementCoordinatorSession, ref failure);
            if (placementCoordinatorSession is null)
                appliedPlacementCoordinatorPlan = null;
        }
        if (memoryModeWorkspace is null && computeScoringWorkspace is null
            && nativeWorkspace is null && pendingNativeWorkspaceClose is null)
            appliedNativeRuntimePlan = null;
        return failure;
    }

    private static void DisposeOwnedResource<T>(ref T? resource, ref Exception? failure)
        where T : class, IDisposable
    {
        var current = resource;
        if (current is null)
        {
            return;
        }

        try
        {
            current.Dispose();
            resource = null;
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
