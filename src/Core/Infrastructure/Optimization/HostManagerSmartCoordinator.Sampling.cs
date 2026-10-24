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
    private readonly List<SchedulingFactExclusion> cycleFactExclusions = [];
    private ulong roundNumber;
    private CapturedSourceIdentities? previousRoundSources;

    private sealed record CapturedSourceIdentities(
        (string Dataset, ulong Workspace, ulong Generation, SamplingObservationStatus Status)[] HardwarePublications,
        (string Dataset, ulong Generation, SamplingObservationStatus Status)[] ProcessPublications,
        (long Session, DateTimeOffset CapturedAt)? Residency);

    private Task<HostManagerSampleCaptureResult> CaptureHostManagerSampleAsync(
        CompiledRuntimePlan runtimePlan,
        CancellationToken cancellationToken)
    {
        cycleFactExclusions.Clear();
        ArgumentNullException.ThrowIfNull(runtimePlan);
        var mode = runtimePlan.OptimizationMode;
        var hardware = metricSnapshotSource.ReadLatest(
            CreateSmartSchedulingMetricRequest(mode, HasAvailablePublicResourceLifecycle()));
        if (hardware is null)
        {
            return Task.FromResult(HostManagerSampleCaptureResult.Unavailable(
                "hardware-snapshot-unavailable"));
        }
        var requestedMetricMask = SchedulingProcessMetricMask.RuntimeState;
        if (mode.MemorySchedulingEnabled || mode.CpuSchedulingEnabled)
        {
            requestedMetricMask |= SchedulingProcessMetricMask.CpuUsage;
        }
        if (mode.AutomaticMemoryCleanupEnabled)
        {
            requestedMetricMask |= SchedulingProcessMetricMask.MemoryUsage;
        }
        if (mode.GpuSchedulingEnabled
            && hardware.GpuInventory.Status == SamplingObservationStatus.Current)
        {
            requestedMetricMask |= SchedulingProcessMetricMask.GpuUsage
                | SchedulingProcessMetricMask.GpuDedicatedMemory;
        }

        var processFacts = schedulingProcessFactSource.ReadLatest(
            new SchedulingProcessFactRequest(
                requestedMetricMask,
                hardware.GpuInventory));
        if (processFacts is null)
        {
            return Task.FromResult(HostManagerSampleCaptureResult.Unavailable(
                "scheduling-process-snapshot-unavailable"));
        }
        var residency = mode.MemorySchedulingEnabled || mode.CpuSchedulingEnabled
            ? cpuCoreResidencyReader.Read()
            : null;
        var identities = new CapturedSourceIdentities(
            CaptureHardwarePublicationIdentities(hardware),
            processFacts.FoundationDatasetObservations.Values
                .Select(static observation => (observation.DatasetId,
                    observation.SourceGeneration, observation.Status))
                .Concat(processFacts.DatasetObservations.Values
                    .Select(static observation => (observation.DatasetId,
                        observation.SourceGeneration, observation.Status)))
                .OrderBy(static identity => identity.DatasetId, StringComparer.Ordinal)
                .ToArray(),
            residency is null ? null : (residency.SessionGeneration, residency.CapturedAt));
        processFacts = processFacts.ProjectValidFacts(hardware.GpuInventory);
        cycleFactExclusions.AddRange(processFacts.FactExclusions);
        var targetInfos = BuildTargetInfos(processFacts, runtimePlan).ToArray();
        AcceptCapturedSources(identities);
        return Task.FromResult(HostManagerSampleCaptureResult.Available(
            new HostManagerSample(
                roundNumber,
                hardware,
                CreateIdleCapacity(hardware),
                processFacts,
                targetInfos,
                residency)));
    }

    // Normal mode subscribes only to capacity. It shares the capture-owned round
    // without making public-resource maintenance depend on process sampling.
    private (HardwareMetricSnapshot Hardware, ulong RoundNumber)? CapturePublicResourceCapacitySample()
    {
        var hardware = metricSnapshotSource.ReadLatest(CreatePublicResourceMetricRequest());
        if (hardware is null) return null;
        var identities = new CapturedSourceIdentities(
            CaptureHardwarePublicationIdentities(hardware),
            previousRoundSources?.ProcessPublications ?? [],
            previousRoundSources?.Residency);
        AcceptCapturedSources(identities);
        return (hardware, roundNumber);
    }

    private static (string Dataset, ulong Workspace, ulong Generation, SamplingObservationStatus Status)[]
        CaptureHardwarePublicationIdentities(HardwareMetricSnapshot hardware)
        => hardware.Datasets.Values
            .Select(static observation => (observation.DatasetId, observation.WorkspaceIdentity,
                observation.SourceGeneration, observation.Status))
            .OrderBy(static identity => identity.DatasetId, StringComparer.Ordinal)
            .ToArray();

    private void AcceptCapturedSources(CapturedSourceIdentities identities)
    {
        if (previousRoundSources is null
            || !identities.HardwarePublications.AsSpan().SequenceEqual(previousRoundSources.HardwarePublications)
            || !identities.ProcessPublications.AsSpan().SequenceEqual(previousRoundSources.ProcessPublications)
            || identities.Residency != previousRoundSources.Residency)
        {
            roundNumber = checked(roundNumber + 1);
            previousRoundSources = identities;
        }
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

    private static MetricSampleRequest CreateSmartSchedulingMetricRequest(
        CompiledOptimizationModePlan mode,
        bool publicResourcesAvailable)
    {
        var ids = new List<string>();
        if (mode.MemorySchedulingEnabled || mode.CpuSchedulingEnabled)
        {
            ids.Add(SamplingDatasetIds.SystemCpuUsage);
        }
        if (mode.MemorySchedulingEnabled || publicResourcesAvailable)
        {
            ids.Add(SamplingDatasetIds.SystemMemoryUsage);
        }
        if (mode.MemorySchedulingEnabled)
        {
            ids.Add(SamplingDatasetIds.SystemVirtualMemoryUsage);
        }
        return mode.GpuSchedulingEnabled || publicResourcesAvailable
            ? MetricSampleRequest.ForIdsAndAllGpuCoreMetrics(ids)
            : MetricSampleRequest.ForIds(ids);
    }

    private static MetricSampleRequest CreatePublicResourceMetricRequest()
        => CreateSmartSchedulingMetricRequest(
            CompiledOptimizationModePlan.Default,
            publicResourcesAvailable: true);

    private static IEnumerable<HostManagerTargetInfo> BuildTargetInfos(
        SchedulingProcessFactSnapshot snapshot,
        CompiledRuntimePlan runtimePlan)
    {
        var byTarget = new Dictionary<string, MutableHostManagerTargetInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in snapshot.Processes)
        {
            if (!process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.RuntimeState)
                || process.ProcessId <= 0 || string.IsNullOrWhiteSpace(process.SoftwareId)
                || string.IsNullOrWhiteSpace(process.ProcessName)
                || !double.IsFinite(process.BaseScore) || process.BaseScore < 0)
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
