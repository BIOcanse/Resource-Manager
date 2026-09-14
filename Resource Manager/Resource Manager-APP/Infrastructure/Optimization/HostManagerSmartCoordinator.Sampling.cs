using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization.Scoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private Task<HostManagerSampleCaptureResult> CaptureHostManagerSampleAsync(
        CompiledRuntimePlan runtimePlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimePlan);
        var hardware = metricSnapshotSource.ReadLatest(
            CreateSmartSchedulingMetricRequest());
        if (hardware is null)
        {
            return Task.FromResult(HostManagerSampleCaptureResult.Unavailable(
                "hardware-snapshot-unavailable"));
        }
        var requestedMetricMask = SchedulingProcessMetricMask.CpuUsage
            | SchedulingProcessMetricMask.RuntimeState;
        if (hardware.GpuInventory.Status == SamplingObservationStatus.Current)
        {
            requestedMetricMask |= SchedulingProcessMetricMask.GpuUsage
                | SchedulingProcessMetricMask.GpuDedicatedMemory;
        }

        var processFacts = schedulingProcessFactSource.ReadLatest(
            new SchedulingProcessFactRequest(
                requestedMetricMask,
                ExpectedMemoryUsageDependency: null,
                hardware.GpuInventory));
        if (processFacts is null)
        {
            return Task.FromResult(HostManagerSampleCaptureResult.Unavailable(
                "scheduling-process-snapshot-unavailable"));
        }
        if (!processFacts.IsRuntimeStateCurrentComplete())
        {
            return Task.FromResult(HostManagerSampleCaptureResult.Unavailable(
                "scheduling-runtime-state-incomplete"));
        }
        processFacts = ProjectSchedulableProcessFacts(processFacts);
        var targetInfos = BuildTargetInfos(processFacts, runtimePlan).ToArray();
        return Task.FromResult(HostManagerSampleCaptureResult.Available(
            new HostManagerSample(
                hardware,
                CreateIdleCapacity(hardware),
                processFacts,
                targetInfos,
                cpuCoreResidencyReader.Read())));
    }

    private sealed record HostManagerSampleCaptureResult(
        HostManagerSample? Sample,
        string? UnavailableReason)
    {
        public static HostManagerSampleCaptureResult Available(
            HostManagerSample sample)
            => new(sample, null);

        public static HostManagerSampleCaptureResult Unavailable(
            string reason)
            => new(null, reason);
    }

    private static MetricSampleRequest CreateSmartSchedulingMetricRequest()
    {
        return MetricSampleRequest.ForIdsAndAllGpuCoreMetrics(
        [
            SamplingDatasetIds.SystemCpuUsage,
            SamplingDatasetIds.SystemMemoryUsage,
            SamplingDatasetIds.SystemVirtualMemoryUsage
        ]);
    }

    private static IEnumerable<HostManagerTargetInfo> BuildTargetInfos(
        SchedulingProcessFactSnapshot snapshot,
        CompiledRuntimePlan runtimePlan)
    {
        var byTarget = new Dictionary<string, MutableHostManagerTargetInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in snapshot.Processes)
        {
            if (process.ProcessId <= 0 || string.IsNullOrWhiteSpace(process.SoftwareId))
            {
                continue;
            }

            var targetId = HostManagerTargetIdentity.CreateProcessTargetId(
                process.ProcessId,
                process.ProcessStartKey);
            if (!byTarget.TryGetValue(targetId, out var target))
            {
                var processKey = OptimizationBaseScorePolicyResolver.CreateProcessKey(
                    process.ProcessName,
                    process.ExecutablePath);
                target = new MutableHostManagerTargetInfo(
                    targetId,
                    process.SoftwareId,
                    CreateProcessDisplayName(process),
                    process.SoftwareKind,
                    process.SoftwareDisplayKind,
                    process.BaseScore,
                    runtimePlan.GpuPlacement.Resolve(
                        process.SoftwareId,
                        process.SoftwareName,
                        process.SoftwareKind,
                        processKey),
                    HostManagerTargetCapabilityClassifier.CanWriteProcessPolicy(
                        process.SoftwareId,
                        process.SoftwareKind),
                    HostManagerTargetCapabilityClassifier.CanDispatchAdapter(
                        process.SoftwareId,
                        process.SoftwareKind),
                    HostManagerTargetCapabilityClassifier.CanWritePhysicalPlacement(
                        process.SoftwareId,
                        process.SoftwareKind),
                    runtimePlan.AdapterDispatch.ResolveRoute(process.SoftwareId),
                    runtimePlan.AdapterDispatch.ResolveSupportedCpuGrades(process.SoftwareId),
                    runtimePlan.AdapterDispatch.ResolveSupportedGpuGrades(process.SoftwareId));
                byTarget.Add(targetId, target);
            }

            target.AddFact(process);
        }

        return byTarget.Values.Select(static target => target.ToImmutable());
    }

    private static SchedulingProcessFactSnapshot ProjectSchedulableProcessFacts(
        SchedulingProcessFactSnapshot source)
    {
        var processes = source.Processes
            .Where(static process => process.ValidMetricMask.HasFlag(
                SchedulingProcessMetricMask.RuntimeState))
            .ToArray();
        var removed = checked(source.Processes.Count - processes.Length);
        if (removed == 0)
        {
            return source;
        }

        return source with
        {
            EmittedCount = checked((uint)processes.Length),
            SkippedCount = checked(source.SkippedCount + (uint)removed),
            Processes = processes
        };
    }

    private static HostManagerIdleCapacitySnapshot CreateIdleCapacity(HardwareMetricSnapshot hardware)
    {
        var memoryFreeRatioCurrent = hardware.TryGetCurrentDataset(
                SamplingDatasetIds.SystemMemoryUsage,
                out _)
            && hardware.Memory.IsUsageAvailable
            && hardware.Memory.ObservationStatus == SamplingObservationStatus.Current
            && hardware.Memory.TotalBytes > 0
            && hardware.Memory.UsedBytes <= hardware.Memory.TotalBytes
            && IsPercent(hardware.Memory.UsagePercent);
        var memoryFreeRatio = memoryFreeRatioCurrent
            ? ClampRatio(1 - hardware.Memory.UsagePercent / 100d)
            : 0;
        return new HostManagerIdleCapacitySnapshot(
            memoryFreeRatio,
            memoryFreeRatioCurrent);
    }

    private static string CreateProcessDisplayName(SchedulingProcessFact process)
    {
        var processName = string.IsNullOrWhiteSpace(process.ProcessName)
            ? $"PID {process.ProcessId}"
            : process.ProcessName;
        return processName.Equals(process.SoftwareName, StringComparison.OrdinalIgnoreCase)
            ? $"{processName} ({process.ProcessId})"
            : $"{process.SoftwareName} / {processName} ({process.ProcessId})";
    }
}
