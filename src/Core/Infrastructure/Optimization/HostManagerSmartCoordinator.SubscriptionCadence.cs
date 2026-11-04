using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private HostManagerSchedulingSubscriptions? smartSubscriptions;
    private IDisposable? rendererObservation;

    private Task SynchronizeSmartSubscriptionsAsync(CancellationToken cancellationToken)
    {
        if (smartSubscriptions is null) return Task.CompletedTask;
        var plan = runtimePlanProvider.Current;
        var mode = plan.OptimizationMode;
        // Cached negative qualifications need continuous lifecycle/image
        // invalidation between rounds, but only while this run can use them.
        var observeRenderers = mode.GpuPlacementEnabled
            && plan.GpuPlacement.GlobalPreciseProviderEnabled
            && !plan.Diagnostics.HostManagerSmartCoordinatorScoreOnlyEnabled;
        if (observeRenderers) rendererObservation ??= runningGpuPlacementActions.AcquireRendererObservation();
        else Interlocked.Exchange(ref rendererObservation, null)?.Dispose();
        var smart = plan.HostManager.RequirePublished().SmartCoordinator.HotPublish;
        var interval = TimeSpan.FromMilliseconds(nativeWorkspace?.Snapshot.EventBoostRoundsRemaining > 0
            ? smart.EventIntervalMilliseconds : smart.NormalIntervalMilliseconds);
        var cpu = mode.MemorySchedulingEnabled || mode.CpuSchedulingEnabled || runtimeCpuLeases.HasLeases;
        var mask = SchedulingProcessMetricMask.RuntimeState;
        if (cpu) mask |= SchedulingProcessMetricMask.CpuUsage;
        if (mode.AutomaticMemoryCleanupEnabled) mask |= SchedulingProcessMetricMask.MemoryUsage;
        if (mode.GpuSchedulingEnabled) mask |= SchedulingProcessMetricMask.GpuUsage | SchedulingProcessMetricMask.GpuDedicatedMemory;
        var process = mode.SchedulingEnabled || runtimeCpuLeases.HasLeases || smart.RealtimeActionBudget.Mode == AppRealtimeActionBudgetModes.Percent;
        var request = mode.SchedulingEnabled ? CreateSmartSchedulingMetricRequest(mode) : MetricSampleRequest.ForIds([]);
        if (runtimeCpuLeases.HasLeases && request.IsEmpty)
            request = CreateSmartSchedulingMetricRequest(CompiledOptimizationModePlan.Compile(AppOptimizationModes.CpuOnly));
        return smartSubscriptions.UpdateAsync(cpu, process ? mask : SchedulingProcessMetricMask.None,
            request, interval, cancellationToken);
    }
}
