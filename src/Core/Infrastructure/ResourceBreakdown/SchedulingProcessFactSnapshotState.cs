using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal sealed class SchedulingProcessFactSnapshotState
{
    private static readonly SchedulingProcessMetricMask[] MetricBits =
    [
        SchedulingProcessMetricMask.CpuUsage,
        SchedulingProcessMetricMask.MemoryUsage,
        SchedulingProcessMetricMask.GpuUsage,
        SchedulingProcessMetricMask.GpuDedicatedMemory,
        SchedulingProcessMetricMask.RuntimeState
    ];

    private readonly object gate;
    private Dictionary<SchedulingProcessMetricMask, DatasetPublication> datasets = [];
    // One row owns the identity, attribution and all current metric values for a
    // process instance. Dataset entries contain publication metadata only.
    private Dictionary<(int ProcessId, ulong ProcessStartKey), ProcessRow> processes = [];
    private FoundationPublication? inventory;
    private FoundationPublication? attribution;
    private ulong scheduleWorkspaceIncarnation;
    private ulong scheduleConfigurationGeneration;

    public SchedulingProcessFactSnapshotState(object? publicationGate = null)
    {
        gate = publicationGate ?? new object();
    }

    public void ApplyScheduled(
        ResourceBreakdownSampleRequest request,
        SchedulingProcessFactSnapshot sample,
        DateTimeOffset attemptedAt,
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sample);
        lock (gate)
        {
            AcceptScheduleUnsafe(schedule);
            var publicationDatasetIds = ResolvePublicationDatasetIds(request);
            if (publicationDatasetIds.Contains(
                    SamplingDatasetIds.ProcessInventory))
            {
                inventory ??= new FoundationPublication(
                    SamplingDatasetIds.ProcessInventory);
                ApplyFoundation(
                    inventory,
                    sample,
                    attemptedAt,
                    sample.HasSeparateFoundationPayloads
                        ? sample.InventoryProcesses
                        : sample.Processes);
            }
            if (publicationDatasetIds.Contains(
                    SamplingDatasetIds.ProcessAttribution))
            {
                attribution ??= new FoundationPublication(
                    SamplingDatasetIds.ProcessAttribution);
                ApplyFoundation(
                    attribution,
                    sample,
                    attemptedAt,
                    sample.HasSeparateFoundationPayloads
                        ? sample.AttributionProcesses
                        : sample.Processes);
            }

            foreach (var bit in EnumerateBits(request.SchedulingMetricMask))
            {
                var dataset = GetOrCreate(bit);
                dataset.LastAttemptAt = attemptedAt;
                if (sample.InventoryStatus != SamplingObservationStatus.Current
                    || !sample.CurrentMetricMask.HasFlag(bit)
                    || !sample.TryGetCurrentDataset(bit, out var observation))
                {
                    MarkDatasetFailed(
                        dataset,
                        attemptedAt,
                        "scheduling-process-dataset-not-current",
                        $"Scheduling process dataset '{DatasetId(bit)}' did not publish a current value.");
                    continue;
                }

                ClearMetric(bit);
                foreach (var process in sample.Processes.Where(process => process.ValidMetricMask.HasFlag(bit)))
                {
                    var key = (process.ProcessId, process.ProcessStartKey);
                    var row = GetOrCreateProcess(process);
                    processes[key] = row with { Fact = MergeMetric(row.Fact, process, bit) };
                }
                dataset.Status = SamplingObservationStatus.Current;
                dataset.SourceGeneration = observation.SourceGeneration;
                dataset.ObservedAtUtcTicks = observation.ObservedAtUtcTicks;
                dataset.InventoryGeneration = observation.InventoryGeneration;
                dataset.InventoryObservedAtUtcTicks =
                    observation.InventoryObservedAtUtcTicks;
                dataset.TopologyGeneration = observation.TopologyGeneration;
                dataset.TopologyFingerprint = observation.TopologyFingerprint;
                dataset.LastSuccessAt = new DateTimeOffset(
                    observation.ObservedAtUtcTicks,
                    TimeSpan.Zero);
                dataset.ReadyUntil = null;
                dataset.FailureCode = null;
                dataset.FailureMessage = null;
            }
        }
    }

    private void ApplyFoundation(
        FoundationPublication foundation,
        SchedulingProcessFactSnapshot sample,
        DateTimeOffset attemptedAt,
        IReadOnlyList<SchedulingProcessFact> sourceProcesses)
    {
        foundation.LastAttemptAt = attemptedAt;
        SchedulingProcessFoundationDatasetObservation? observation = null;
        var hasCurrentObservation = !sample.HasSeparateFoundationPayloads
            ? sample.InventoryStatus == SamplingObservationStatus.Current
            : sample.TryGetCurrentFoundationDataset(
                foundation.DatasetId,
                out observation);
        if (sample.InventoryStatus != SamplingObservationStatus.Current
            || !hasCurrentObservation)
        {
            MarkFoundationFailed(
                foundation,
                attemptedAt,
                "scheduling-process-foundation-dataset-not-current",
                $"Scheduling process foundation dataset '{foundation.DatasetId}' did not publish a current value.");
            return;
        }

        var sourceGeneration = sample.HasSeparateFoundationPayloads
            ? observation!.SourceGeneration
            : sample.Generation;
        var observedAtUtcTicks = sample.HasSeparateFoundationPayloads
            ? observation!.ObservedAtUtcTicks
            : sample.ObservedAtUtcTicks;
        var present = sourceProcesses.Select(static process =>
            (process.ProcessId, process.ProcessStartKey)).ToHashSet();
        if (present.Count != sourceProcesses.Count)
            throw new ArgumentException("A process foundation contains duplicate process instances.", nameof(sourceProcesses));
        ClearFoundation(foundation.DatasetId, present);
        foreach (var process in sourceProcesses)
        {
            var key = (process.ProcessId, process.ProcessStartKey);
            var row = GetOrCreateProcess(process);
            var identity = foundation.DatasetId == SamplingDatasetIds.ProcessInventory
                ? row.Fact with
                {
                    ProcessName = ReuseText(row.Fact.ProcessName, process.ProcessName)!,
                    ParentProcessId = process.ParentProcessId,
                    ExecutablePath = ReuseText(row.Fact.ExecutablePath, process.ExecutablePath)
                }
                : ComposeIdentity(row.Fact, process);
            processes[key] = row with
            {
                Fact = identity,
                HasInventory = row.HasInventory || foundation.DatasetId == SamplingDatasetIds.ProcessInventory,
                HasAttribution = row.HasAttribution || foundation.DatasetId == SamplingDatasetIds.ProcessAttribution
            };
        }
        foundation.SoftwareBaseScores = foundation.DatasetId == SamplingDatasetIds.ProcessAttribution
            ? sample.SoftwareBaseScores : [];
        foundation.Status = SamplingObservationStatus.Current;
        foundation.SourceGeneration = sourceGeneration;
        foundation.ObservedAtUtcTicks = observedAtUtcTicks;
        foundation.EnumeratedCount = sample.EnumeratedCount;
        foundation.ExcludedCount = sample.ExcludedCount;
        foundation.HasUnidentifiedProcesses = sample.HasUnidentifiedProcesses;
        foundation.SkippedCount = sample.SkippedCount;
        foundation.OverflowCount = sample.OverflowCount;
        foundation.LastSuccessAt = new DateTimeOffset(
            observedAtUtcTicks,
            TimeSpan.Zero);
        foundation.ReadyUntil = null;
        foundation.FailureCode = null;
        foundation.FailureMessage = null;
    }

    internal void AcceptSchedule(
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        lock (gate)
        {
            AcceptScheduleUnsafe(schedule);
        }
    }

    internal SchedulingProcessFactSnapshotState PrepareScheduledCopy(
        ResourceBreakdownSampleRequest request,
        SchedulingProcessFactSnapshot sample,
        DateTimeOffset attemptedAt,
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        lock (gate)
        {
            var prepared = CloneUnsafe();
            prepared.ApplyScheduled(request, sample, attemptedAt, schedule);
            return prepared;
        }
    }

    internal SchedulingProcessFactSnapshotState PrepareFailureCopy(
        ResourceBreakdownSampleRequest request,
        DateTimeOffset attemptedAt,
        Exception error)
    {
        lock (gate)
        {
            var prepared = CloneUnsafe();
            prepared.MarkFailed(request, attemptedAt, error);
            return prepared;
        }
    }

    internal bool RequestedDatasetsCommitted(
        ResourceBreakdownSampleRequest request,
        SchedulingProcessFactSnapshot sample)
    {
        lock (gate)
        {
            var publicationDatasetIds = ResolvePublicationDatasetIds(request);
            if (publicationDatasetIds.Contains(
                    SamplingDatasetIds.ProcessInventory)
                && !FoundationCommitted(
                    inventory,
                    sample,
                    SamplingDatasetIds.ProcessInventory)
                || publicationDatasetIds.Contains(
                    SamplingDatasetIds.ProcessAttribution)
                && !FoundationCommitted(
                    attribution,
                    sample,
                    SamplingDatasetIds.ProcessAttribution))
            {
                return false;
            }
            foreach (var bit in EnumerateBits(request.SchedulingMetricMask))
            {
                if (!sample.TryGetCurrentDataset(bit, out var observation))
                {
                    return false;
                }
                if (!datasets.TryGetValue(bit, out var dataset)
                    || dataset.Status != SamplingObservationStatus.Current
                    || dataset.FailureCode is not null
                    || dataset.SourceGeneration != observation.SourceGeneration
                    || dataset.ObservedAtUtcTicks != observation.ObservedAtUtcTicks
                    || dataset.LastSuccessAt is null)
                {
                    return false;
                }
            }
            return true;
        }
    }

    private static bool FoundationCommitted(
        FoundationPublication? foundation,
        SchedulingProcessFactSnapshot sample,
        string datasetId)
    {
        if (foundation is null
            || foundation.Status != SamplingObservationStatus.Current
            || foundation.FailureCode is not null
            || foundation.LastSuccessAt is null)
        {
            return false;
        }
        if (!sample.HasSeparateFoundationPayloads)
        {
            return foundation.SourceGeneration == sample.Generation
                && foundation.ObservedAtUtcTicks == sample.ObservedAtUtcTicks;
        }
        return sample.TryGetCurrentFoundationDataset(datasetId, out var observation)
            && foundation.SourceGeneration == observation.SourceGeneration
            && foundation.ObservedAtUtcTicks == observation.ObservedAtUtcTicks;
    }

    internal void CommitPrepared(SchedulingProcessFactSnapshotState prepared)
    {
        lock (gate)
        {
            datasets = prepared.datasets;
            processes = prepared.processes;
            inventory = prepared.inventory;
            attribution = prepared.attribution;
            scheduleWorkspaceIncarnation = prepared.scheduleWorkspaceIncarnation;
            scheduleConfigurationGeneration =
                prepared.scheduleConfigurationGeneration;
        }
    }

    private void MarkFailed(
        ResourceBreakdownSampleRequest request,
        DateTimeOffset attemptedAt,
        Exception error)
    {
        var signature = $"{error.GetType().FullName}:{error.HResult}";
        var publicationDatasetIds = ResolvePublicationDatasetIds(request);
        if (publicationDatasetIds.Contains(SamplingDatasetIds.ProcessInventory))
        {
            inventory ??= new FoundationPublication(SamplingDatasetIds.ProcessInventory);
            MarkFoundationFailed(
                inventory,
                attemptedAt,
                "scheduling-process-foundation-dataset-publication-failed",
                signature);
        }
        if (publicationDatasetIds.Contains(SamplingDatasetIds.ProcessAttribution))
        {
            attribution ??= new FoundationPublication(SamplingDatasetIds.ProcessAttribution);
            MarkFoundationFailed(
                attribution,
                attemptedAt,
                "scheduling-process-foundation-dataset-publication-failed",
                signature);
        }
        foreach (var bit in EnumerateBits(request.SchedulingMetricMask))
        {
            var dataset = GetOrCreate(bit);
            MarkDatasetFailed(
                dataset,
                attemptedAt,
                "scheduling-process-dataset-publication-failed",
                signature);
        }
    }

    private void MarkFoundationFailed(
        FoundationPublication foundation,
        DateTimeOffset attemptedAt,
        string failureCode,
        string failureMessage)
    {
        foundation.LastAttemptAt = attemptedAt;
        ClearFoundation(foundation.DatasetId);
        foundation.SoftwareBaseScores = [];
        foundation.Status = SamplingObservationStatus.Unavailable;
        foundation.SourceGeneration = 0;
        foundation.ObservedAtUtcTicks = 0;
        foundation.EnumeratedCount = 0;
        foundation.ExcludedCount = 0;
        foundation.HasUnidentifiedProcesses = false;
        foundation.SkippedCount = 0;
        foundation.OverflowCount = 0;
        foundation.LastSuccessAt = null;
        foundation.ReadyUntil = null;
        foundation.FailureCode = failureCode;
        foundation.FailureMessage = failureMessage;
    }

    private void MarkDatasetFailed(
        DatasetPublication dataset,
        DateTimeOffset attemptedAt,
        string failureCode,
        string failureMessage)
    {
        ClearMetric(dataset.Bit);
        dataset.Status = SamplingObservationStatus.Unavailable;
        dataset.SourceGeneration = 0;
        dataset.ObservedAtUtcTicks = 0;
        dataset.InventoryGeneration = 0;
        dataset.InventoryObservedAtUtcTicks = 0;
        dataset.TopologyGeneration = 0;
        dataset.TopologyFingerprint = 0;
        dataset.LastAttemptAt = attemptedAt;
        dataset.LastSuccessAt = null;
        dataset.ReadyUntil = null;
        dataset.FailureCode = failureCode;
        dataset.FailureMessage = failureMessage;
    }

    private SchedulingProcessFactSnapshotState CloneUnsafe()
    {
        var clone = new SchedulingProcessFactSnapshotState(gate)
        {
            datasets = datasets.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Clone()),
            processes = new(processes),
            inventory = inventory?.Clone(),
            attribution = attribution?.Clone(),
            scheduleWorkspaceIncarnation = scheduleWorkspaceIncarnation,
            scheduleConfigurationGeneration = scheduleConfigurationGeneration
        };
        return clone;
    }

    public IReadOnlyList<RuntimeSoftwareAttribution> ReadLatestSoftware()
    {
        lock (gate)
        {
            if (inventory is null || attribution is null
                || !IsFoundationCurrent(inventory) || !IsFoundationCurrent(attribution))
                return [];

            return processes.Values
                .Where(static row => row.HasInventory && row.HasAttribution)
                .Select(static row => row.Fact)
                .GroupBy(static process => process.SoftwareId, StringComparer.OrdinalIgnoreCase)
                .Select(static group =>
                {
                    // A category fact wins over discovery-only kinds for the software
                    // view, regardless of process enumeration order.
                    var selected = group
                        .OrderBy(static process => process.SoftwareKind is SoftwareKinds.RuntimePackage
                            or SoftwareKinds.RuntimeProduct or SoftwareKinds.RuntimeRoot ? 1 : 0)
                        .ThenBy(static process => process.SoftwareKind, StringComparer.Ordinal)
                        .ThenBy(static process => process.SoftwareName, StringComparer.Ordinal)
                        .ThenBy(static process => process.SoftwareCategorySource, StringComparer.Ordinal)
                        .First();
                    return new RuntimeSoftwareAttribution(
                        group.Key, selected.SoftwareName, selected.SoftwareKind, selected.SoftwareDisplayKind,
                        group.SelectMany(static process => process.SoftwareRootPaths)
                            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                        selected.SoftwareCategorySource);
                })
                .OrderBy(static software => software.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public SchedulingProcessFactSnapshot? ReadLatest(
        SchedulingProcessFactRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = now;
        lock (gate)
        {
            var requestedBits = EnumerateBits(request.RequestedMetricMask).ToArray();
            if (inventory is null)
            {
                return null;
            }
            if (!IsFoundationCurrent(inventory))
            {
                return null;
            }
            if (attribution is null)
            {
                return null;
            }
            if (!IsFoundationCurrent(attribution))
            {
                return null;
            }
            if (requestedBits.Length > 0 && requestedBits.All(bit => !datasets.ContainsKey(bit)))
            {
                return null;
            }

            var currentMask = SchedulingProcessMetricMask.None;
            var selected = new List<DatasetPublication>(requestedBits.Length);
            var observations = new Dictionary<
                SchedulingProcessMetricMask,
                SchedulingProcessDatasetObservation>();
            foreach (var bit in requestedBits)
            {
                if (!datasets.TryGetValue(bit, out var dataset))
                {
                    continue;
                }
                var current = dataset.Status == SamplingObservationStatus.Current
                    && dataset.FailureCode is null
                    && dataset.LastSuccessAt is not null;
                var failureCode = current || dataset.FailureCode is not null
                    ? dataset.FailureCode
                    : "scheduling-process-dataset-not-published";
                observations.Add(
                    bit,
                    dataset.CreateObservation(
                        current
                            ? SamplingObservationStatus.Current
                            : SamplingObservationStatus.Unavailable,
                        failureCode));
                if (current)
                {
                    selected.Add(dataset);
                    currentMask |= bit;
                }
            }

            if (requestedBits.Length > 0 && selected.Count == 0)
            {
                return null;
            }

            var generation = inventory.SourceGeneration;
            var currentProcesses = processes.Values
                .Where(row => row.HasInventory && row.HasAttribution
                    && (row.Fact.ValidMetricMask & currentMask) != SchedulingProcessMetricMask.None)
                .Select(row => Project(row.Fact, row.Fact.ValidMetricMask & currentMask)
                    with { SourceGeneration = generation })
                .OrderBy(static process => process.ProcessId)
                .ThenBy(static process => process.ProcessStartKey)
                .ToArray();
            var gpuDataset = selected
                    .FirstOrDefault(static dataset =>
                        dataset.Bit == SchedulingProcessMetricMask.GpuUsage)
                ?? selected.FirstOrDefault(static dataset =>
                    dataset.Bit ==
                        SchedulingProcessMetricMask.GpuDedicatedMemory);
            var emitted = checked((uint)currentProcesses.Length);
            return new SchedulingProcessFactSnapshot(
                SamplingObservationStatus.Current,
                generation,
                inventory.ObservedAtUtcTicks,
                inventory.EnumeratedCount,
                emitted,
                checked(inventory.SkippedCount
                    + (uint)Math.Max(
                        0,
                        processes.Values.Count(static row => row.HasInventory) - currentProcesses.Length)),
                inventory.OverflowCount,
                request.RequestedMetricMask,
                currentMask,
                currentProcesses,
                gpuDataset?.SourceGeneration ?? 0,
                gpuDataset?.ObservedAtUtcTicks ?? 0,
                gpuDataset?.TopologyGeneration ?? 0,
                gpuDataset?.TopologyFingerprint ?? 0,
                inventory.ExcludedCount)
            {
                HasSeparateFoundationPayloads = true,
                HasUnidentifiedProcesses = inventory.HasUnidentifiedProcesses,
                InventoryProcesses = processes.Values.Where(static row => row.HasInventory)
                    .Select(static row => ProjectInventory(row.Fact)).ToArray(),
                AttributionProcesses = processes.Values.Where(static row => row.HasAttribution)
                    .Select(static row => ProjectAttribution(row.Fact)).ToArray(),
                SoftwareBaseScores = attribution.SoftwareBaseScores,
                FoundationDatasetObservations = new Dictionary<
                    string,
                    SchedulingProcessFoundationDatasetObservation>(
                        StringComparer.OrdinalIgnoreCase)
                {
                    [SamplingDatasetIds.ProcessInventory] =
                        inventory.CreateObservation(),
                    [SamplingDatasetIds.ProcessAttribution] =
                        attribution.CreateObservation()
                },
                DatasetObservations = observations
            };
        }
    }

    private static bool IsFoundationCurrent(
        FoundationPublication foundation)
        => foundation.Status == SamplingObservationStatus.Current
            && foundation.FailureCode is null
            && foundation.LastSuccessAt is not null;

    private static SchedulingProcessFact ComposeIdentity(
        SchedulingProcessFact inventoryProcess,
        SchedulingProcessFact attributionProcess)
        => inventoryProcess with
        {
            SoftwareId = ReuseText(inventoryProcess.SoftwareId, attributionProcess.SoftwareId)!,
            SoftwareName = ReuseText(inventoryProcess.SoftwareName, attributionProcess.SoftwareName)!,
            SoftwareKind = ReuseText(inventoryProcess.SoftwareKind, attributionProcess.SoftwareKind)!,
            SoftwareDisplayKind = ReuseText(inventoryProcess.SoftwareDisplayKind, attributionProcess.SoftwareDisplayKind)!,
            SoftwareRootPaths = inventoryProcess.SoftwareRootPaths.SequenceEqual(attributionProcess.SoftwareRootPaths, StringComparer.Ordinal)
                ? inventoryProcess.SoftwareRootPaths : attributionProcess.SoftwareRootPaths,
            SoftwareCategorySource = ReuseText(inventoryProcess.SoftwareCategorySource, attributionProcess.SoftwareCategorySource),
            BaseScore = attributionProcess.BaseScore
        };

    private static string? ReuseText(string? current, string? incoming)
        => string.Equals(current, incoming, StringComparison.Ordinal) ? current : incoming;

    private readonly record struct ProcessRow(
        SchedulingProcessFact Fact,
        bool HasInventory = false,
        bool HasAttribution = false);

    private ProcessRow GetOrCreateProcess(SchedulingProcessFact source)
    {
        var key = (source.ProcessId, source.ProcessStartKey);
        if (!processes.TryGetValue(key, out var row))
        {
            row = new ProcessRow(ProjectInventory(source));
            processes.Add(key, row);
        }
        return row;
    }

    private void ClearFoundation(
        string datasetId,
        HashSet<(int ProcessId, ulong ProcessStartKey)>? preserved = null)
    {
        foreach (var key in processes.Keys.ToArray())
        {
            if (preserved?.Contains(key) == true) continue;
            var row = processes[key];
            row = datasetId == SamplingDatasetIds.ProcessInventory
                ? row with { HasInventory = false }
                : row with { HasAttribution = false, Fact = row.Fact with
                {
                    SoftwareId = string.Empty, SoftwareName = string.Empty,
                    SoftwareKind = string.Empty, SoftwareDisplayKind = string.Empty,
                    SoftwareRootPaths = [], SoftwareCategorySource = null, BaseScore = 0
                } };
            StoreOrRemove(key, row);
        }
    }

    private void ClearMetric(SchedulingProcessMetricMask bit)
    {
        foreach (var key in processes.Keys.ToArray())
        {
            var row = processes[key];
            if (row.Fact.ValidMetricMask.HasFlag(bit))
                row = row with { Fact = Project(row.Fact, row.Fact.ValidMetricMask & ~bit) };
            StoreOrRemove(key, row);
        }
    }

    private void StoreOrRemove((int ProcessId, ulong ProcessStartKey) key, ProcessRow row)
    {
        if (!row.HasInventory && !row.HasAttribution
            && row.Fact.ValidMetricMask == SchedulingProcessMetricMask.None)
            processes.Remove(key);
        else
            processes[key] = row;
    }

    private DatasetPublication GetOrCreate(SchedulingProcessMetricMask bit)
    {
        if (!datasets.TryGetValue(bit, out var dataset))
        {
            dataset = new DatasetPublication(bit);
            datasets.Add(bit, dataset);
        }
        return dataset;
    }

    private void AcceptScheduleUnsafe(
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        scheduleWorkspaceIncarnation = schedule.WorkspaceIncarnation;
        scheduleConfigurationGeneration = schedule.ConfigurationGeneration;
    }

    private static SchedulingProcessFact MergeMetric(
        SchedulingProcessFact current,
        SchedulingProcessFact incoming,
        SchedulingProcessMetricMask bit)
    {
        var mask = current.ValidMetricMask | bit;
        switch (bit)
        {
            case SchedulingProcessMetricMask.CpuUsage:
                return current with { ValidMetricMask = mask, CpuUsagePercent = incoming.CpuUsagePercent };
            case SchedulingProcessMetricMask.MemoryUsage:
                return current with { ValidMetricMask = mask, MemoryUsedBytes = incoming.MemoryUsedBytes };
            case SchedulingProcessMetricMask.RuntimeState:
                return current with
                {
                    ValidMetricMask = mask, RuntimeState = incoming.RuntimeState,
                    ForegroundFocused = incoming.ForegroundFocused, HasVisibleWindow = incoming.HasVisibleWindow,
                    HasBackgroundWindow = incoming.HasBackgroundWindow, HasHiddenWindow = incoming.HasHiddenWindow
                };
            default:
                var merged = new MutableProcess(current);
                merged.Merge(current);
                merged.Merge(Project(incoming, bit));
                return merged.Build(current.SourceGeneration);
        }
    }

    private static SchedulingProcessFact Project(
        SchedulingProcessFact process,
        SchedulingProcessMetricMask bit)
        => process with
        {
            ValidMetricMask = bit,
            CpuUsagePercent = bit.HasFlag(SchedulingProcessMetricMask.CpuUsage)
                ? process.CpuUsagePercent
                : 0,
            MemoryUsedBytes = bit.HasFlag(SchedulingProcessMetricMask.MemoryUsage)
                ? process.MemoryUsedBytes
                : 0,
            Gpus = (bit & (SchedulingProcessMetricMask.GpuUsage
                | SchedulingProcessMetricMask.GpuDedicatedMemory)) != 0
                    ? process.Gpus
                        .Where(gpu => (bit.HasFlag(SchedulingProcessMetricMask.GpuUsage) && gpu.HasMetric(SchedulingProcessMetricMask.GpuUsage))
                            || (bit.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory) && gpu.HasMetric(SchedulingProcessMetricMask.GpuDedicatedMemory)))
                        .Select(gpu => gpu with
                        {
                            ValidMetricMask = gpu.ValidMetricMask & bit,
                            PrivateMemoryBytes = bit.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
                                ? gpu.PrivateMemoryBytes : null,
                            SharedMemoryBytes = bit.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
                                ? gpu.SharedMemoryBytes : null,
                            UsagePercent = bit.HasFlag(SchedulingProcessMetricMask.GpuUsage)
                                ? gpu.UsagePercent
                                : 0,
                            DedicatedMemoryUsedPercent =
                                bit.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
                                    ? gpu.DedicatedMemoryUsedPercent
                                    : 0,
                            UsageSourceGeneration =
                                bit.HasFlag(SchedulingProcessMetricMask.GpuUsage)
                                    ? gpu.UsageSourceGeneration
                                    : 0,
                            DedicatedMemorySourceGeneration =
                                bit.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
                                    ? gpu.DedicatedMemorySourceGeneration
                                    : 0,
                            UsageTopologyGeneration =
                                bit.HasFlag(SchedulingProcessMetricMask.GpuUsage)
                                    ? gpu.UsageTopologyGeneration
                                    : 0,
                            DedicatedMemoryTopologyGeneration =
                                bit.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
                                    ? gpu.DedicatedMemoryTopologyGeneration
                                    : 0
                        })
                        .ToArray()
                    : [],
            RuntimeState = bit.HasFlag(SchedulingProcessMetricMask.RuntimeState)
                ? process.RuntimeState
                : HostManagerRuntimeStates.Unknown,
            ForegroundFocused = bit.HasFlag(SchedulingProcessMetricMask.RuntimeState)
                && process.ForegroundFocused,
            HasVisibleWindow = bit.HasFlag(SchedulingProcessMetricMask.RuntimeState)
                && process.HasVisibleWindow,
            HasBackgroundWindow = bit.HasFlag(SchedulingProcessMetricMask.RuntimeState)
                && process.HasBackgroundWindow,
            HasHiddenWindow = bit.HasFlag(SchedulingProcessMetricMask.RuntimeState)
                && process.HasHiddenWindow
        };

    private static SchedulingProcessFact ProjectInventory(
        SchedulingProcessFact process)
        => process with
        {
            SoftwareId = string.Empty,
            SoftwareName = string.Empty,
            SoftwareKind = string.Empty,
            SoftwareDisplayKind = string.Empty,
            SoftwareRootPaths = [],
            SoftwareCategorySource = null,
            BaseScore = 0,
            ValidMetricMask = SchedulingProcessMetricMask.None,
            CpuUsagePercent = 0,
            MemoryUsedBytes = 0,
            Gpus = [],
            RuntimeState = HostManagerRuntimeStates.Unknown,
            ForegroundFocused = false,
            HasVisibleWindow = false,
            HasBackgroundWindow = false,
            HasHiddenWindow = false
        };

    private static SchedulingProcessFact ProjectAttribution(
        SchedulingProcessFact process)
        => process with
        {
            ValidMetricMask = SchedulingProcessMetricMask.None,
            CpuUsagePercent = 0,
            MemoryUsedBytes = 0,
            Gpus = [],
            RuntimeState = HostManagerRuntimeStates.Unknown,
            ForegroundFocused = false,
            HasVisibleWindow = false,
            HasBackgroundWindow = false,
            HasHiddenWindow = false
        };

    private static IReadOnlySet<string> ResolvePublicationDatasetIds(
        ResourceBreakdownSampleRequest request)
    {
        if (request.PublicationDatasetIds.Count > 0)
        {
            return request.PublicationDatasetIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
        }

        var result = SamplingDatasetIds.ForSchedulingMetrics(
                request.SchedulingMetricMask)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.SchedulingMetricMask != SchedulingProcessMetricMask.None)
        {
            result.Add(SamplingDatasetIds.ProcessInventory);
            result.Add(SamplingDatasetIds.ProcessAttribution);
        }
        return result;
    }

    private static IEnumerable<SchedulingProcessMetricMask> EnumerateBits(
        SchedulingProcessMetricMask mask)
    {
        foreach (var bit in MetricBits)
        {
            if (mask.HasFlag(bit))
            {
                yield return bit;
            }
        }
    }

    private static string DatasetId(SchedulingProcessMetricMask bit)
        => SchedulingProcessFactSnapshot.DatasetIdFor(bit);

    private sealed class DatasetPublication(SchedulingProcessMetricMask bit)
    {
        public SchedulingProcessMetricMask Bit { get; } = bit;
        public SamplingObservationStatus Status { get; set; } =
            SamplingObservationStatus.Unavailable;
        public ulong SourceGeneration { get; set; }
        public long ObservedAtUtcTicks { get; set; }
        public ulong InventoryGeneration { get; set; }
        public long InventoryObservedAtUtcTicks { get; set; }
        public ulong TopologyGeneration { get; set; }
        public ulong TopologyFingerprint { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public DateTimeOffset? ReadyUntil { get; set; }
        public string? FailureCode { get; set; }
        public string? FailureMessage { get; set; }

        public DatasetPublication Clone()
        {
            return new DatasetPublication(Bit)
            {
                Status = Status,
                SourceGeneration = SourceGeneration,
                ObservedAtUtcTicks = ObservedAtUtcTicks,
                InventoryGeneration = InventoryGeneration,
                InventoryObservedAtUtcTicks = InventoryObservedAtUtcTicks,
                TopologyGeneration = TopologyGeneration,
                TopologyFingerprint = TopologyFingerprint,
                LastAttemptAt = LastAttemptAt,
                LastSuccessAt = LastSuccessAt,
                ReadyUntil = ReadyUntil,
                FailureCode = FailureCode,
                FailureMessage = FailureMessage
            };
        }

        public SchedulingProcessDatasetObservation CreateObservation(
            SamplingObservationStatus status,
            string? failureCode)
            => new(
                Bit,
                DatasetId(Bit),
                status,
                SourceGeneration,
                ObservedAtUtcTicks,
                InventoryGeneration,
                InventoryObservedAtUtcTicks)
            {
                LastAttemptAtUtcTicks = LastAttemptAt?.UtcTicks ?? 0,
                LastSuccessAtUtcTicks = LastSuccessAt?.UtcTicks ?? 0,
                ReadyUntilUtcTicks = ReadyUntil?.UtcTicks ?? 0,
                FailureCode = failureCode,
                FailureMessage = FailureMessage,
                TopologyGeneration = TopologyGeneration,
                TopologyFingerprint = TopologyFingerprint
            };
    }

    private sealed class FoundationPublication(string datasetId)
    {
        public System.Collections.Immutable.ImmutableArray<SoftwareBaseScore> SoftwareBaseScores
            { get; set; } = [];
        public string DatasetId { get; } = datasetId;
        public SamplingObservationStatus Status { get; set; } =
            SamplingObservationStatus.Unavailable;
        public ulong SourceGeneration { get; set; }
        public long ObservedAtUtcTicks { get; set; }
        public uint EnumeratedCount { get; set; }
        public uint ExcludedCount { get; set; }
        public bool HasUnidentifiedProcesses { get; set; }
        public uint SkippedCount { get; set; }
        public uint OverflowCount { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public DateTimeOffset? ReadyUntil { get; set; }
        public string? FailureCode { get; set; }
        public string? FailureMessage { get; set; }

        public FoundationPublication Clone()
            => new(DatasetId)
            {
                SoftwareBaseScores = SoftwareBaseScores,
                Status = Status,
                SourceGeneration = SourceGeneration,
                ObservedAtUtcTicks = ObservedAtUtcTicks,
                EnumeratedCount = EnumeratedCount,
                ExcludedCount = ExcludedCount,
                HasUnidentifiedProcesses = HasUnidentifiedProcesses,
                SkippedCount = SkippedCount,
                OverflowCount = OverflowCount,
                LastAttemptAt = LastAttemptAt,
                LastSuccessAt = LastSuccessAt,
                ReadyUntil = ReadyUntil,
                FailureCode = FailureCode,
                FailureMessage = FailureMessage
            };

        public SchedulingProcessFoundationDatasetObservation CreateObservation()
            => new(
                DatasetId,
                IsFoundationCurrent(this)
                    ? SamplingObservationStatus.Current
                    : SamplingObservationStatus.Unavailable,
                SourceGeneration,
                ObservedAtUtcTicks)
            {
                LastAttemptAtUtcTicks = LastAttemptAt?.UtcTicks ?? 0,
                LastSuccessAtUtcTicks = LastSuccessAt?.UtcTicks ?? 0,
                ReadyUntilUtcTicks = ReadyUntil?.UtcTicks ?? 0,
                FailureCode = FailureCode,
                FailureMessage = FailureMessage
            };
    }

    private sealed class MutableProcess
    {
        private readonly SchedulingProcessFact identity;
        private SchedulingProcessMetricMask validMask;
        private double cpuUsagePercent;
        private double memoryUsedBytes;
        private string runtimeState = HostManagerRuntimeStates.Unknown;
        private bool foregroundFocused;
        private bool hasVisibleWindow;
        private bool hasBackgroundWindow;
        private bool hasHiddenWindow;
        private readonly Dictionary<(int Index, ulong Key), SchedulingProcessGpuFact> gpus = [];

        public MutableProcess(SchedulingProcessFact identity)
        {
            this.identity = identity;
        }

        public void Merge(SchedulingProcessFact process)
        {
            validMask |= process.ValidMetricMask;
            if (process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage))
            {
                cpuUsagePercent = process.CpuUsagePercent;
            }
            if (process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.MemoryUsage))
            {
                memoryUsedBytes = process.MemoryUsedBytes;
            }
            if (process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.RuntimeState))
            {
                runtimeState = process.RuntimeState;
                foregroundFocused = process.ForegroundFocused;
                hasVisibleWindow = process.HasVisibleWindow;
                hasBackgroundWindow = process.HasBackgroundWindow;
                hasHiddenWindow = process.HasHiddenWindow;
            }
            foreach (var gpu in process.Gpus)
            {
                var key = (gpu.GpuIndex, gpu.AdapterKey);
                if (!gpus.TryGetValue(key, out var current))
                {
                    gpus.Add(key, gpu);
                    continue;
                }
                gpus[key] = current with
                {
                    ValidMetricMask = current.ValidMetricMask | gpu.ValidMetricMask,
                    PrivateMemoryBytes = process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
                        ? gpu.PrivateMemoryBytes : current.PrivateMemoryBytes,
                    SharedMemoryBytes = process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
                        ? gpu.SharedMemoryBytes : current.SharedMemoryBytes,
                    UsagePercent = gpu.ValidMetricMask.HasFlag(
                        SchedulingProcessMetricMask.GpuUsage)
                            ? gpu.UsagePercent
                            : current.UsagePercent,
                    DedicatedMemoryUsedPercent = gpu.ValidMetricMask.HasFlag(
                        SchedulingProcessMetricMask.GpuDedicatedMemory)
                            ? gpu.DedicatedMemoryUsedPercent
                            : current.DedicatedMemoryUsedPercent,
                    UsageSourceGeneration = gpu.ValidMetricMask.HasFlag(
                        SchedulingProcessMetricMask.GpuUsage)
                            ? gpu.UsageSourceGeneration
                            : current.UsageSourceGeneration,
                    DedicatedMemorySourceGeneration =
                        gpu.HasMetric(
                            SchedulingProcessMetricMask.GpuDedicatedMemory)
                            ? gpu.DedicatedMemorySourceGeneration
                            : current.DedicatedMemorySourceGeneration,
                    UsageTopologyGeneration = gpu.ValidMetricMask.HasFlag(
                        SchedulingProcessMetricMask.GpuUsage)
                            ? gpu.UsageTopologyGeneration
                            : current.UsageTopologyGeneration,
                    DedicatedMemoryTopologyGeneration =
                        gpu.HasMetric(
                            SchedulingProcessMetricMask.GpuDedicatedMemory)
                            ? gpu.DedicatedMemoryTopologyGeneration
                            : current.DedicatedMemoryTopologyGeneration
                };
            }
        }

        public SchedulingProcessFact Build(ulong generation)
            => identity with
            {
                ValidMetricMask = validMask,
                CpuUsagePercent = cpuUsagePercent,
                MemoryUsedBytes = memoryUsedBytes,
                SourceGeneration = generation,
                Gpus = gpus.Values
                    .OrderBy(static gpu => gpu.GpuIndex)
                    .ThenBy(static gpu => gpu.AdapterKey)
                    .ToArray(),
                RuntimeState = runtimeState,
                ForegroundFocused = foregroundFocused,
                HasVisibleWindow = hasVisibleWindow,
                HasBackgroundWindow = hasBackgroundWindow,
                HasHiddenWindow = hasHiddenWindow
            };
    }
}
