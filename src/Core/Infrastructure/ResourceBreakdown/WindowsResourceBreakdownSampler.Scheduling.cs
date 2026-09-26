using ResourceManager.App.Application.Optimization.Scoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private const SchedulingProcessMetricMask SupportedSchedulingMetricMask =
        SchedulingProcessMetricMask.CpuUsage
        | SchedulingProcessMetricMask.MemoryUsage
        | SchedulingProcessMetricMask.GpuUsage
        | SchedulingProcessMetricMask.GpuDedicatedMemory
        | SchedulingProcessMetricMask.RuntimeState;

    public async Task<SchedulingProcessFactSnapshot> CaptureAsync(
        SchedulingProcessFactRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.GpuInventory);
        if (request.RequestedMetricMask == SchedulingProcessMetricMask.None
            || (request.RequestedMetricMask & ~SupportedSchedulingMetricMask) != 0)
        {
            throw new ArgumentException("The scheduling process request contains no metrics or unknown metric bits.", nameof(request));
        }

        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            return await CaptureSchedulingSnapshotCoreAsync(request, cancellationToken);
        }
        finally
        {
            sampleGate.Release();
        }
    }

    private async Task<SchedulingProcessFactSnapshot> CaptureSchedulingSnapshotCoreAsync(
        SchedulingProcessFactRequest request,
        CancellationToken cancellationToken)
    {
        var generation = checked((ulong)Interlocked.Increment(ref schedulingProcessGeneration));
        var observedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        ProcessSampleBatch batch;
        try
        {
            batch = CaptureProcessSamples(
                request.RequestedMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage),
                ProcessSampleDetailLevel.SmartSchedulingLite,
                directProcessCpuDeltaTracker);
        }
        catch
        {
            return CreateUnavailableSchedulingSnapshot(request, generation, observedAtUtcTicks);
        }

        if (batch.Status != SamplingObservationStatus.Current)
        {
            return new SchedulingProcessFactSnapshot(
                batch.Status,
                generation,
                observedAtUtcTicks,
                batch.EnumeratedCount,
                0,
                batch.SkippedCount,
                0,
                request.RequestedMetricMask,
                SchedulingProcessMetricMask.None,
                [],
                ExcludedCount: batch.ExcludedCount);
        }

        ProcessAttributionSnapshot attribution;
        try
        {
            var attributionCatalog =
                await processAttributionCatalogProvider.GetCatalogAsync(
                    cancellationToken);
            attribution = CreateProcessAttributionSnapshot(
                batch.Samples,
                attributionCatalog);
        }
        catch
        {
            return new SchedulingProcessFactSnapshot(
                SamplingObservationStatus.Unavailable,
                generation,
                observedAtUtcTicks,
                batch.EnumeratedCount,
                0,
                checked(batch.EnumeratedCount - batch.ExcludedCount),
                0,
                request.RequestedMetricMask,
                SchedulingProcessMetricMask.None,
                [],
                ExcludedCount: batch.ExcludedCount);
        }

        return CreateSchedulingSnapshot(
            request,
            batch,
            attribution,
            generation,
            observedAtUtcTicks,
            allocationReading: request.RequestedMetricMask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
                ? ReadGpuAllocations(batch.Samples) : null);
    }

    private SchedulingProcessFactSnapshot CreateSchedulingSnapshot(
        SchedulingProcessFactRequest request,
        ProcessSampleBatch batch,
        ProcessAttributionSnapshot? attribution,
        ulong generation,
        long observedAtUtcTicks,
        bool preserveUnattributedMetricRows = false,
        ResourceManager.App.Infrastructure.Telemetry.Etw.GpuAllocationReading? allocationReading = null)
    {
        var requestedGpuMask = request.RequestedMetricMask
            & (SchedulingProcessMetricMask.GpuUsage | SchedulingProcessMetricMask.GpuDedicatedMemory);
        var processInstances = batch.Samples
            .Where(static sample => sample.ProcessId > 0 && sample.StartKey is > 0)
            .Select(static sample => new ProcessInstanceKey(sample.ProcessId, sample.StartKey!.Value))
            .ToArray();
        var gpuRead = requestedGpuMask == SchedulingProcessMetricMask.None
            ? SchedulingProcessGpuRead.NotRequested
            : processGpuReader.ReadSchedulingSnapshot(request.GpuInventory, processInstances,
                includeDedicatedMemory: false);
        if (requestedGpuMask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory))
            gpuRead = GpuAllocationSchedulingProjection.Apply(gpuRead, allocationReading, processInstances);
        var runtimeStateRead = request.RequestedMetricMask.HasFlag(
                SchedulingProcessMetricMask.RuntimeState)
            ? WindowsProcessRuntimeStateSnapshot.Capture(processInstances)
            : WindowsProcessRuntimeStateSnapshot.NotRequested;
        var baseScorePlan = runtimePlanProvider.Current.BaseScore;
        var facts = new List<SchedulingProcessFact>(batch.Samples.Count);
        var inventoryFacts = new List<SchedulingProcessFact>(batch.Samples.Count);
        var attributionFacts = new List<SchedulingProcessFact>(batch.Samples.Count);
        var excludedCount = batch.ExcludedCount;
        var skippedCount = batch.SkippedCount;
        foreach (var sample in batch.Samples)
        {
            if (sample.ProcessId <= 0 || sample.StartKey is not > 0)
            {
                excludedCount++;
                continue;
            }

            var processName = string.IsNullOrWhiteSpace(sample.Name)
                ? $"PID {sample.ProcessId}"
                : sample.Name;
            var inventoryFact = new SchedulingProcessFact(
                sample.ProcessId,
                checked((ulong)sample.StartKey.Value),
                processName,
                sample.ExecutablePath,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                SchedulingProcessMetricMask.None,
                0,
                0,
                generation,
                []);
            inventoryFacts.Add(inventoryFact);

            var software = attribution?.AttributionByProcessId.GetValueOrDefault(
                    sample.ProcessId)
                ?? RuntimeSoftwareAttribution.Unattributed;
            var attributed = !string.IsNullOrWhiteSpace(software.Id)
                && !software.Id.Equals(
                    RuntimeAttributionIds.Unattributed,
                    StringComparison.OrdinalIgnoreCase);
            var identityFact = inventoryFact;
            if (attributed)
            {
                var processKey = OptimizationBaseScorePolicyResolver.CreateProcessKey(
                    sample.Name,
                    sample.ExecutablePath);
                identityFact = inventoryFact with
                {
                    SoftwareId = software.Id,
                    SoftwareName = software.Name,
                    SoftwareKind = software.Kind,
                    SoftwareDisplayKind = software.DisplayKind,
                    BaseScore = baseScorePlan.ResolveBaseScore(
                        software.Id,
                        software.Kind,
                        processKey)
                };
                attributionFacts.Add(identityFact);
            }
            else if (!preserveUnattributedMetricRows)
            {
                skippedCount++;
                continue;
            }

            var validMask = SchedulingProcessMetricMask.None;
            var cpuUsagePercent = 0d;
            if (request.RequestedMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage)
                && sample.HasCpuPercent)
            {
                validMask |= SchedulingProcessMetricMask.CpuUsage;
                cpuUsagePercent = Math.Clamp(sample.CpuPercent, 0, 100);
            }

            var memoryUsagePercent = 0d;
            if (request.RequestedMetricMask.HasFlag(SchedulingProcessMetricMask.MemoryUsage)
                && request.ExpectedMemoryUsageDependency is { } memoryDependency
                && memoryDependency.IsCurrentAt(new DateTimeOffset(
                    observedAtUtcTicks,
                    TimeSpan.Zero))
                && sample.HasWorkingSetBytes)
            {
                validMask |= SchedulingProcessMetricMask.MemoryUsage;
                memoryUsagePercent = Math.Clamp(
                    Math.Max(0, sample.WorkingSetBytes) * 100d
                        / memoryDependency.DenominatorBytes,
                    0,
                    100);
            }

            var processInstance = new ProcessInstanceKey(
                sample.ProcessId,
                sample.StartKey.Value);
            runtimeStateRead.Processes.TryGetValue(
                processInstance,
                out var runtimeState);
            if (request.RequestedMetricMask.HasFlag(
                    SchedulingProcessMetricMask.RuntimeState)
                && runtimeStateRead.Status == SamplingObservationStatus.Current
                && runtimeState is not null)
            {
                validMask |= SchedulingProcessMetricMask.RuntimeState;
            }

            var gpuFacts = CreateSchedulingGpuFacts(
                request,
                gpuRead,
                sample.ProcessId,
                checked((ulong)sample.StartKey.Value));
            if (requestedGpuMask.HasFlag(SchedulingProcessMetricMask.GpuUsage)
                && IsGpuMetricComplete(
                    request.GpuInventory,
                    gpuFacts,
                    SchedulingProcessMetricMask.GpuUsage,
                    requireDedicatedCapability: false))
            {
                validMask |= SchedulingProcessMetricMask.GpuUsage;
            }
            if (requestedGpuMask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
                && IsGpuMetricComplete(
                    request.GpuInventory,
                    gpuFacts,
                    SchedulingProcessMetricMask.GpuDedicatedMemory,
                    requireDedicatedCapability: true))
            {
                validMask |= SchedulingProcessMetricMask.GpuDedicatedMemory;
            }

            facts.Add(identityFact with
            {
                ValidMetricMask = validMask,
                CpuUsagePercent = cpuUsagePercent,
                MemoryUsagePercent = memoryUsagePercent,
                Gpus = gpuFacts,
                RuntimeState = runtimeState?.RuntimeState
                    ?? HostManagerRuntimeStates.BackgroundProcess,
                ForegroundFocused = runtimeState?.ForegroundFocused == true,
                HasVisibleWindow = runtimeState?.HasVisibleWindow == true,
                HasBackgroundWindow = runtimeState?.HasBackgroundWindow == true,
                HasHiddenWindow = runtimeState?.HasHiddenWindow == true
            });
        }

        var currentMetricMask = ResolveCurrentMetricMask(
            request,
            facts,
            gpuRead,
            runtimeStateRead);
        var datasetObservations = CreateDatasetObservations(
            request,
            currentMetricMask,
            generation,
            observedAtUtcTicks,
            gpuRead);
        var legacyGpuGeneration = currentMetricMask.HasFlag(
                SchedulingProcessMetricMask.GpuUsage)
            ? gpuRead.UsageGeneration
            : currentMetricMask.HasFlag(
                SchedulingProcessMetricMask.GpuDedicatedMemory)
                ? gpuRead.DedicatedMemoryGeneration
                : 0;
        var legacyGpuObservedAt = currentMetricMask.HasFlag(
                SchedulingProcessMetricMask.GpuUsage)
            ? gpuRead.UsageObservedAtUtcTicks
            : currentMetricMask.HasFlag(
                SchedulingProcessMetricMask.GpuDedicatedMemory)
                ? gpuRead.DedicatedMemoryObservedAtUtcTicks
                : 0;

        return new SchedulingProcessFactSnapshot(
            SamplingObservationStatus.Current,
            generation,
            observedAtUtcTicks,
            batch.EnumeratedCount,
            checked((uint)facts.Count),
            skippedCount,
            0,
            request.RequestedMetricMask,
            currentMetricMask,
            facts,
            legacyGpuGeneration,
            legacyGpuObservedAt,
            requestedGpuMask == SchedulingProcessMetricMask.None ? 0 : request.GpuInventory.Generation,
            requestedGpuMask == SchedulingProcessMetricMask.None ? 0 : request.GpuInventory.TopologyFingerprint,
            excludedCount)
        {
            HasSeparateFoundationPayloads = true,
            InventoryProcesses = inventoryFacts,
            AttributionProcesses = attributionFacts,
            SoftwareBaseScores = attribution?.Catalog.GetSoftwareBaseScores(baseScorePlan) ?? [],
            FoundationDatasetObservations = CreateFoundationDatasetObservations(
                generation,
                observedAtUtcTicks,
                attribution is not null),
            DatasetObservations = datasetObservations
        };
    }

    private static IReadOnlyDictionary<
        string,
        SchedulingProcessFoundationDatasetObservation>
        CreateFoundationDatasetObservations(
            ulong generation,
            long observedAtUtcTicks,
            bool attributionCurrent)
    {
        var result = new Dictionary<
            string,
            SchedulingProcessFoundationDatasetObservation>(
                StringComparer.OrdinalIgnoreCase)
        {
            [SamplingDatasetIds.ProcessInventory] =
                SchedulingProcessFoundationDatasetObservation.CreateCurrent(
                    SamplingDatasetIds.ProcessInventory,
                    generation,
                    observedAtUtcTicks)
        };
        if (attributionCurrent)
        {
            result[SamplingDatasetIds.ProcessAttribution] =
                SchedulingProcessFoundationDatasetObservation.CreateCurrent(
                    SamplingDatasetIds.ProcessAttribution,
                    generation,
                    observedAtUtcTicks);
        }
        return result;
    }

    internal static SchedulingProcessMetricMask ResolveCurrentMetricMask(
        SchedulingProcessFactRequest request,
        IReadOnlyList<SchedulingProcessFact> facts,
        SchedulingProcessGpuRead gpuRead,
        WindowsProcessRuntimeStateSnapshot runtimeStateRead)
    {
        var current = SchedulingProcessMetricMask.None;
        if (request.RequestedMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage))
        {
            current |= SchedulingProcessMetricMask.CpuUsage;
        }
        if (request.RequestedMetricMask.HasFlag(SchedulingProcessMetricMask.MemoryUsage)
            && request.ExpectedMemoryUsageDependency is { } memoryDependency
            && memoryDependency.IsCurrentAt(DateTimeOffset.UtcNow))
        {
            current |= SchedulingProcessMetricMask.MemoryUsage;
        }
        if (gpuRead.UsageStatus == SamplingObservationStatus.Current
            && gpuRead.UsageGeneration > 0
            && request.RequestedMetricMask.HasFlag(
                SchedulingProcessMetricMask.GpuUsage))
        {
            current |= SchedulingProcessMetricMask.GpuUsage;
        }
        if (gpuRead.DedicatedMemoryStatus
                == SamplingObservationStatus.Current
            && gpuRead.DedicatedMemoryGeneration > 0
            && request.RequestedMetricMask.HasFlag(
                SchedulingProcessMetricMask.GpuDedicatedMemory))
        {
            current |= SchedulingProcessMetricMask.GpuDedicatedMemory;
        }
        if (request.RequestedMetricMask.HasFlag(
                SchedulingProcessMetricMask.RuntimeState)
            && runtimeStateRead.Status == SamplingObservationStatus.Current)
        {
            current |= SchedulingProcessMetricMask.RuntimeState;
        }
        return current;
    }

    internal static IReadOnlyList<SchedulingProcessGpuFact> CreateSchedulingGpuFacts(
        SchedulingProcessFactRequest request,
        SchedulingProcessGpuRead gpuRead,
        int processId,
        ulong processStartKey)
    {
        if ((gpuRead.UsageStatus != SamplingObservationStatus.Current
                || gpuRead.UsageGeneration == 0)
            && (gpuRead.DedicatedMemoryStatus
                    != SamplingObservationStatus.Current
                || gpuRead.DedicatedMemoryGeneration == 0)
            || !request.GpuInventory.IsCurrentComplete())
        {
            return [];
        }

        var facts = new List<SchedulingProcessGpuFact>(request.GpuInventory.Adapters.Count);
        foreach (var adapter in request.GpuInventory.Adapters)
        {
            var validMask = SchedulingProcessMetricMask.None;
            var usagePercent = 0d;
            var usageSourceGeneration = 0UL;
            var usageTopologyGeneration = 0UL;
            var key = (adapter.AdapterKey, processId, processStartKey);
            if (request.RequestedMetricMask.HasFlag(SchedulingProcessMetricMask.GpuUsage)
                && gpuRead.UsageStatus == SamplingObservationStatus.Current
                && gpuRead.UsageGeneration > 0
                && adapter.UsageStatus == SamplingObservationStatus.Current
                && gpuRead.UsagePercent.TryGetValue(key, out var measuredUsage))
            {
                validMask |= SchedulingProcessMetricMask.GpuUsage;
                usageSourceGeneration = gpuRead.UsageGeneration;
                usageTopologyGeneration = request.GpuInventory.Generation;
                usagePercent = Math.Clamp(
                    measuredUsage,
                    0,
                    100);
            }

            var dedicatedMemoryPercent = 0d;
            var dedicatedMemorySourceGeneration = 0UL;
            var dedicatedMemoryTopologyGeneration = 0UL;
            var allocation = request.RequestedMetricMask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
                && gpuRead.DedicatedMemoryStatus == SamplingObservationStatus.Current
                && gpuRead.DedicatedMemoryGeneration > 0
                    ? gpuRead.AllocationAmounts.GetValueOrDefault(key)
                    : null;
            if (allocation is not null)
            {
                dedicatedMemorySourceGeneration = gpuRead.DedicatedMemoryGeneration;
                dedicatedMemoryTopologyGeneration = request.GpuInventory.Generation;
            }
            if (request.RequestedMetricMask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
                && gpuRead.DedicatedMemoryStatus
                    == SamplingObservationStatus.Current
                && gpuRead.DedicatedMemoryGeneration > 0
                && adapter.CapacityStatus == SamplingObservationStatus.Current
                && adapter.TotalDedicatedMemoryBytes > 0
                && gpuRead.DedicatedMemoryBytes.TryGetValue(
                    key,
                    out var measuredDedicatedBytes))
            {
                validMask |= SchedulingProcessMetricMask.GpuDedicatedMemory;
                dedicatedMemorySourceGeneration =
                    gpuRead.DedicatedMemoryGeneration;
                dedicatedMemoryTopologyGeneration =
                    request.GpuInventory.Generation;
                dedicatedMemoryPercent = Math.Clamp(
                    measuredDedicatedBytes
                        * 100d / adapter.TotalDedicatedMemoryBytes,
                    0,
                    100);
            }

            if (validMask != SchedulingProcessMetricMask.None || allocation is not null)
            {
                facts.Add(new SchedulingProcessGpuFact(
                    adapter.Index,
                    adapter.AdapterKey,
                    validMask,
                    usagePercent,
                    dedicatedMemoryPercent,
                    usageSourceGeneration,
                    dedicatedMemorySourceGeneration,
                    usageTopologyGeneration,
                    dedicatedMemoryTopologyGeneration)
                {
                    PrivateMemoryBytes = allocation?.PrivateBytes,
                    SharedMemoryBytes = allocation?.SharedBytes
                });
            }
        }

        return facts;
    }

    private static IReadOnlyDictionary<
        SchedulingProcessMetricMask,
        SchedulingProcessDatasetObservation> CreateDatasetObservations(
        SchedulingProcessFactRequest request,
        SchedulingProcessMetricMask currentMetricMask,
        ulong inventoryGeneration,
        long inventoryObservedAtUtcTicks,
        SchedulingProcessGpuRead gpuRead)
    {
        var result = new Dictionary<
            SchedulingProcessMetricMask,
            SchedulingProcessDatasetObservation>();
        foreach (var metric in EnumerateMetricBits())
        {
            if (!currentMetricMask.HasFlag(metric))
            {
                continue;
            }

            var (sourceGeneration, observedAtUtcTicks) = metric switch
            {
                SchedulingProcessMetricMask.GpuUsage =>
                    (gpuRead.UsageGeneration, gpuRead.UsageObservedAtUtcTicks),
                SchedulingProcessMetricMask.GpuDedicatedMemory =>
                    (gpuRead.DedicatedMemoryGeneration,
                        gpuRead.DedicatedMemoryObservedAtUtcTicks),
                _ => (inventoryGeneration, inventoryObservedAtUtcTicks)
            };
            var gpuMetric = metric is SchedulingProcessMetricMask.GpuUsage
                or SchedulingProcessMetricMask.GpuDedicatedMemory;
            var memoryDependency = metric == SchedulingProcessMetricMask.MemoryUsage
                ? request.ExpectedMemoryUsageDependency
                : null;
            result.Add(
                metric,
                SchedulingProcessDatasetObservation.CreateCurrent(
                    metric,
                    sourceGeneration,
                    observedAtUtcTicks,
                    inventoryGeneration,
                    inventoryObservedAtUtcTicks,
                    readyUntilUtcTicks: 0,
                    topologyGeneration: gpuMetric
                        ? request.GpuInventory.Generation
                        : 0,
                    topologyFingerprint: gpuMetric
                        ? request.GpuInventory.TopologyFingerprint
                        : 0,
                    memoryUsageDependency: memoryDependency));
        }
        return result;
    }

    private static bool IsGpuMetricComplete(
        SchedulingGpuInventorySnapshot inventory,
        IReadOnlyList<SchedulingProcessGpuFact> facts,
        SchedulingProcessMetricMask metric,
        bool requireDedicatedCapability)
    {
        if (!inventory.IsCurrentComplete())
        {
            return false;
        }

        return facts.Any(fact =>
            fact.HasMetric(metric)
            && inventory.Adapters.Any(adapter =>
                adapter.AdapterKey == fact.AdapterKey
                && adapter.Index == fact.GpuIndex
                && (requireDedicatedCapability
                    ? !fact.ValidMetricMask.HasFlag(metric)
                        || adapter.CapabilityMask.HasFlag(
                            SchedulingGpuCapabilityMask.DedicatedMemory)
                        && adapter.CapacityStatus == SamplingObservationStatus.Current
                        && adapter.TotalDedicatedMemoryBytes > 0
                    : adapter.UsageStatus == SamplingObservationStatus.Current)));
    }

    private static IEnumerable<SchedulingProcessMetricMask> EnumerateMetricBits()
    {
        yield return SchedulingProcessMetricMask.CpuUsage;
        yield return SchedulingProcessMetricMask.MemoryUsage;
        yield return SchedulingProcessMetricMask.GpuUsage;
        yield return SchedulingProcessMetricMask.GpuDedicatedMemory;
        yield return SchedulingProcessMetricMask.RuntimeState;
    }

    private static SchedulingProcessFactSnapshot CreateUnavailableSchedulingSnapshot(
        SchedulingProcessFactRequest request,
        ulong generation,
        long observedAtUtcTicks)
    {
        return new SchedulingProcessFactSnapshot(
            SamplingObservationStatus.Unavailable,
            generation,
            observedAtUtcTicks,
            0,
            0,
            0,
            0,
            request.RequestedMetricMask,
            SchedulingProcessMetricMask.None,
            []);
    }
}
