using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal sealed partial class HostManagerReportCoordinatorOwner
{
    private static readonly ResourceBreakdownSnapshot EmptyResources = new(DateTimeOffset.UnixEpoch, []);
    private CompiledHostManagerReportCoordinatorPlan? observationInputPlan;
    private ReportObservationInputs? observationInputs;
    private ReportObservationInputs? subscribedInputs;

    private ReportObservationInputs GetObservationInputs(CompiledHostManagerReportCoordinatorPlan plan)
    {
        if (!ReferenceEquals(observationInputPlan, plan))
        {
            observationInputs = ReportObservationInputs.ForPlan(plan);
            observationInputPlan = plan;
        }
        return observationInputs!;
    }

    private void UpdateObservationSubscriptions(CompiledHostManagerReportCoordinatorPlan plan)
    {
        if (!subscribed) return;
        var inputs = GetObservationInputs(plan);
        if (ReferenceEquals(subscribedInputs, inputs)) return;
        if (subscribedInputs is { } current
            && current.Metrics.CacheKey == inputs.Metrics.CacheKey
            && current.Resources.CacheKey == inputs.Resources.CacheKey
            && current.ProcessMetrics == inputs.ProcessMetrics
            && current.NeedsInterrupts == inputs.NeedsInterrupts)
        {
            subscribedInputs = inputs;
            if (!subscribed) subscribedInputs = null;
            return;
        }

        var releaseFailures = new List<Exception>();
        ReleaseObservationSubscriptions(releaseFailures);
        if (releaseFailures.Count > 0) ThrowFailures(releaseFailures);
        try
        {
            if (inputs.ProcessMetrics != SchedulingProcessMetricMask.None)
                processSubscription = processSource.AcquireSubscription(
                    "host-manager.reports.runtime-state", inputs.ProcessMetrics, SampleInterval);
            if (inputs.NeedsMetrics)
                metricSubscription = metricSource.AcquireSubscription(
                    "host-manager.reports.hardware", inputs.Metrics, SampleInterval);
            if (inputs.NeedsResources)
                resourceSubscription = resourceSource.AcquireSubscription(
                    "host-manager.reports.software-memory", inputs.Resources, SampleInterval);
            if (inputs.NeedsInterrupts)
                interruptSubscription = interruptSource.AcquireSubscription(
                    "host-manager.reports.system-interrupts", SampleInterval);
            subscribedInputs = inputs;
            // Stop may release earlier leases while a source admits the next
            // one. Release the rest without making a cancelled Stop join work.
            if (!subscribed)
            {
                var failures = new List<Exception>();
                ReleaseObservationSubscriptions(failures);
                if (failures.Count > 0) ThrowFailures(failures);
            }
        }
        catch (Exception acquisitionFailure)
        {
            var failures = new List<Exception> { acquisitionFailure };
            ReleaseObservationSubscriptions(failures);
            ThrowFailures(failures);
        }
    }

    private sealed record ReportObservationInputs(
        MetricSampleRequest Metrics,
        ResourceBreakdownSampleRequest Resources,
        SchedulingProcessMetricMask ProcessMetrics,
        bool NeedsMetrics,
        bool NeedsResources,
        bool NeedsInterrupts)
    {
        internal static ReportObservationInputs ForPlan(CompiledHostManagerReportCoordinatorPlan plan)
        {
            var kinds = plan.Recreate.Rules.Select(rule => rule.FactKind).ToHashSet();
            var cpu = kinds.Any(HostManagerCpuReportFactProjector.Supports);
            var softwareMemory = kinds.Any(HostManagerSoftwareMemoryReportFactProjector.Supports);
            var strayProcess = kinds.Any(HostManagerStrayProcessReportFactProjector.Supports);
            var metrics = new List<string>();
            // The CPU report's predicate group requires all three current datasets.
            if (cpu) metrics.AddRange(["cpu.temperature", "cpu.usage", "cpu.frequencyPercent"]);
            if (strayProcess) metrics.Add(SamplingDatasetIds.SystemGpuInventory);
            var resourceMetrics = new List<string>();
            if (softwareMemory || strayProcess) resourceMetrics.Add(ResourceBreakdownMetricIds.MemoryUsage);
            if (strayProcess) resourceMetrics.Add("process_all_command_line_actual");
            var processMetrics = softwareMemory ? SchedulingProcessMetricMask.RuntimeState : SchedulingProcessMetricMask.None;
            if (strayProcess) processMetrics |= SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.GpuUsage;
            return new ReportObservationInputs(
                MetricSampleRequest.ForIds(metrics),
                ResourceBreakdownSampleRequest.ForResourceTable(resourceMetrics,
                    resourceMetrics.Count == 0 ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [ResourceBreakdownMetricIds.MemoryUsage] = ResourceBreakdownScaleModes.Capacity
                        }),
                processMetrics,
                metrics.Count > 0,
                resourceMetrics.Count > 0,
                kinds.Any(HostManagerInterruptReportFactProjector.Supports));
        }
    }
}
