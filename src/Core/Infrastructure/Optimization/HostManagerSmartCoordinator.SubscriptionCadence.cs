using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private HostManagerSchedulingSubscriptions? smartSubscriptions;

    private Task SynchronizeSmartSubscriptionsAsync(CancellationToken cancellationToken)
    {
        if (smartSubscriptions is null) return Task.CompletedTask;
        var plan = runtimePlanProvider.Current;
        var mode = plan.OptimizationMode;
        var smart = plan.HostManager.RequirePublished().SmartCoordinator.HotPublish;
        var interval = TimeSpan.FromMilliseconds(nativeWorkspace?.Snapshot.EventBoostRoundsRemaining > 0
            ? smart.EventIntervalMilliseconds : smart.NormalIntervalMilliseconds);
        var cpu = mode.MemorySchedulingEnabled || mode.CpuSchedulingEnabled;
        var mask = SchedulingProcessMetricMask.RuntimeState;
        if (cpu) mask |= SchedulingProcessMetricMask.CpuUsage;
        if (mode.AutomaticMemoryCleanupEnabled) mask |= SchedulingProcessMetricMask.MemoryUsage;
        if (mode.GpuSchedulingEnabled) mask |= SchedulingProcessMetricMask.GpuUsage | SchedulingProcessMetricMask.GpuDedicatedMemory;
        var process = mode.SchedulingEnabled || smart.RealtimeActionBudget.Mode == AppRealtimeActionBudgetModes.Percent;
        var publicResources = HasAvailablePublicResourceLifecycle();
        var request = mode.SchedulingEnabled ? CreateSmartSchedulingMetricRequest(mode, publicResources)
            : publicResources ? CreatePublicResourceMetricRequest() : MetricSampleRequest.ForIds([]);
        return smartSubscriptions.UpdateAsync(cpu, process ? mask : SchedulingProcessMetricMask.None,
            request, interval, cancellationToken);
    }
}
