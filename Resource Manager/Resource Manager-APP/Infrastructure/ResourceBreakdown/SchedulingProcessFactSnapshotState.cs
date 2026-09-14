using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
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
    private Dictionary<SchedulingProcessMetricMask, DatasetEntry> datasets = [];
    private FoundationEntry? inventory;
    private FoundationEntry? attribution;
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
                inventory ??= new FoundationEntry(
                    SamplingDatasetIds.ProcessInventory);
                ApplyFoundation(
                    inventory,
                    sample,
                    attemptedAt,
                    sample.HasSeparateFoundationPayloads
                        ? sample.InventoryProcesses
                        : sample.Processes,
                    static process => ProjectInventory(process));
            }
            if (publicationDatasetIds.Contains(
                    SamplingDatasetIds.ProcessAttribution))
            {
                attribution ??= new FoundationEntry(
                    SamplingDatasetIds.ProcessAttribution);
                ApplyFoundation(
                    attribution,
                    sample,
                    attemptedAt,
                    sample.HasSeparateFoundationPayloads
                        ? sample.AttributionProcesses
                        : sample.Processes,
                    static process => ProjectAttribution(process));
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

                var replacement = sample.Processes
                    .Where(process => process.ValidMetricMask.HasFlag(bit))
                    .Select(process => Project(process, bit))
                    .ToDictionary(
                        static process => (process.ProcessId, process.ProcessStartKey));
                dataset.Processes = replacement;
                dataset.Status = SamplingObservationStatus.Current;
                dataset.SourceGeneration = observation.SourceGeneration;
                dataset.ObservedAtUtcTicks = observation.ObservedAtUtcTicks;
                dataset.InventoryGeneration = observation.InventoryGeneration;
                dataset.InventoryObservedAtUtcTicks =
                    observation.InventoryObservedAtUtcTicks;
                dataset.EnumeratedCount = sample.EnumeratedCount;
                dataset.ExcludedCount = sample.ExcludedCount;
                dataset.SkippedCount = sample.SkippedCount;
                dataset.OverflowCount = sample.OverflowCount;
                dataset.TopologyGeneration = observation.TopologyGeneration;
                dataset.TopologyFingerprint = observation.TopologyFingerprint;
                dataset.MemoryUsageDependency =
                    observation.MemoryUsageDependency;
                dataset.LastSuccessAt = new DateTimeOffset(
                    observation.ObservedAtUtcTicks,
                    TimeSpan.Zero);
                dataset.ReadyUntil = null;
                dataset.FailureCode = null;
                dataset.FailureMessage = null;
            }
        }
    }

    private static void ApplyFoundation(
        FoundationEntry foundation,
        SchedulingProcessFactSnapshot sample,
        DateTimeOffset attemptedAt,
        IReadOnlyList<SchedulingProcessFact> sourceProcesses,
        Func<SchedulingProcessFact, SchedulingProcessFact> project)
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
        foundation.Processes = sourceProcesses
            .Select(project)
            .ToDictionary(static process =>
                (process.ProcessId, process.ProcessStartKey));
        foundation.SoftwareBaseScores = sample.SoftwareBaseScores;
        foundation.Status = SamplingObservationStatus.Current;
        foundation.SourceGeneration = sourceGeneration;
        foundation.ObservedAtUtcTicks = observedAtUtcTicks;
        foundation.EnumeratedCount = sample.EnumeratedCount;
        foundation.ExcludedCount = sample.ExcludedCount;
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
                    || dataset.MemoryUsageDependency !=
                        observation.MemoryUsageDependency
                    || dataset.LastSuccessAt is null)
                {
                    return false;
                }
            }
            return true;
        }
    }

    private static bool FoundationCommitted(
        FoundationEntry? foundation,
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
            inventory ??= new FoundationEntry(SamplingDatasetIds.ProcessInventory);
            MarkFoundationFailed(
                inventory,
                attemptedAt,
                "scheduling-process-foundation-dataset-publication-failed",
                signature);
        }
        if (publicationDatasetIds.Contains(SamplingDatasetIds.ProcessAttribution))
        {
            attribution ??= new FoundationEntry(SamplingDatasetIds.ProcessAttribution);
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

    private static void MarkFoundationFailed(
        FoundationEntry foundation,
        DateTimeOffset attemptedAt,
        string failureCode,
        string failureMessage)
    {
        foundation.LastAttemptAt = attemptedAt;
        foundation.Processes.Clear();
        foundation.SoftwareBaseScores = [];
        foundation.Status = SamplingObservationStatus.Unavailable;
        foundation.SourceGeneration = 0;
        foundation.ObservedAtUtcTicks = 0;
        foundation.EnumeratedCount = 0;
        foundation.ExcludedCount = 0;
        foundation.SkippedCount = 0;
        foundation.OverflowCount = 0;
        foundation.LastSuccessAt = null;
        foundation.ReadyUntil = null;
        foundation.FailureCode = failureCode;
        foundation.FailureMessage = failureMessage;
    }

    private static void MarkDatasetFailed(
        DatasetEntry dataset,
        DateTimeOffset attemptedAt,
        string failureCode,
        string failureMessage)
    {
        dataset.Processes.Clear();
        dataset.Status = SamplingObservationStatus.Unavailable;
        dataset.SourceGeneration = 0;
        dataset.ObservedAtUtcTicks = 0;
        dataset.InventoryGeneration = 0;
        dataset.InventoryObservedAtUtcTicks = 0;
        dataset.EnumeratedCount = 0;
        dataset.ExcludedCount = 0;
        dataset.SkippedCount = 0;
        dataset.OverflowCount = 0;
        dataset.TopologyGeneration = 0;
        dataset.TopologyFingerprint = 0;
        dataset.MemoryUsageDependency = null;
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
            inventory = inventory?.Clone(),
            attribution = attribution?.Clone(),
            scheduleWorkspaceIncarnation = scheduleWorkspaceIncarnation,
            scheduleConfigurationGeneration = scheduleConfigurationGeneration
        };
        return clone;
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
            if (requestedBits.Length == 0)
            {
                return null;
            }
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
            if (requestedBits.All(bit => !datasets.ContainsKey(bit)))
            {
                return null;
            }

            var currentMask = SchedulingProcessMetricMask.None;
            var selected = new List<DatasetEntry>(requestedBits.Length);
            var observations = new Dictionary<
                SchedulingProcessMetricMask,
                SchedulingProcessDatasetObservation>();
            foreach (var bit in requestedBits)
            {
                if (!datasets.TryGetValue(bit, out var dataset))
                {
                    continue;
                }
                var dependencyCurrent = bit !=
                        SchedulingProcessMetricMask.MemoryUsage
                    || request.ExpectedMemoryUsageDependency is
                        { } expectedMemoryDependency
                        && expectedMemoryDependency.IsWellFormed()
                        && dataset.MemoryUsageDependency ==
                            expectedMemoryDependency;
                var current = dataset.Status == SamplingObservationStatus.Current
                    && dataset.FailureCode is null
                    && dataset.LastSuccessAt is not null
                    && dependencyCurrent;
                var failureCode = current || dataset.FailureCode is not null
                    ? dataset.FailureCode
                    : !dependencyCurrent
                        ? "scheduling-process-memory-dependency-mismatch"
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

            if (selected.Count == 0)
            {
                return null;
            }

            var processBuilders = new Dictionary<
                (int ProcessId, ulong ProcessStartKey),
                MutableProcess>();
            foreach (var dataset in selected)
            {
                foreach (var process in dataset.Processes.Values)
                {
                    var key = (process.ProcessId, process.ProcessStartKey);
                    if (!inventory.Processes.TryGetValue(
                            key,
                            out var inventoryProcess))
                    {
                        continue;
                    }
                    if (!attribution.Processes.TryGetValue(
                            key,
                            out var attributionProcess))
                    {
                        continue;
                    }
                    if (!processBuilders.TryGetValue(key, out var builder))
                    {
                        builder = new MutableProcess(ComposeIdentity(
                            inventoryProcess,
                            attributionProcess));
                        processBuilders.Add(key, builder);
                    }
                    builder.Merge(process);
                }
            }

            var generation = inventory.SourceGeneration;
            var processes = processBuilders.Values
                .Select(builder => builder.Build(generation))
                .OrderBy(static process => process.ProcessId)
                .ThenBy(static process => process.ProcessStartKey)
                .ToArray();
            var gpuDataset = selected
                    .FirstOrDefault(static dataset =>
                        dataset.Bit == SchedulingProcessMetricMask.GpuUsage)
                ?? selected.FirstOrDefault(static dataset =>
                    dataset.Bit ==
                        SchedulingProcessMetricMask.GpuDedicatedMemory);
            var emitted = checked((uint)processes.Length);
            return new SchedulingProcessFactSnapshot(
                SamplingObservationStatus.Current,
                generation,
                inventory.ObservedAtUtcTicks,
                inventory.EnumeratedCount,
                emitted,
                checked(inventory.SkippedCount
                    + (uint)Math.Max(
                        0,
                        inventory.Processes.Count - processes.Length)),
                inventory.OverflowCount,
                request.RequestedMetricMask,
                currentMask,
                processes,
                gpuDataset?.SourceGeneration ?? 0,
                gpuDataset?.ObservedAtUtcTicks ?? 0,
                gpuDataset?.TopologyGeneration ?? 0,
                gpuDataset?.TopologyFingerprint ?? 0,
                inventory.ExcludedCount)
            {
                HasSeparateFoundationPayloads = true,
                InventoryProcesses = inventory.Processes.Values.ToArray(),
                AttributionProcesses = attribution.Processes.Values.ToArray(),
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
        FoundationEntry foundation)
        => foundation.Status == SamplingObservationStatus.Current
            && foundation.FailureCode is null
            && foundation.LastSuccessAt is not null;

    private static SchedulingProcessFact ComposeIdentity(
        SchedulingProcessFact inventoryProcess,
        SchedulingProcessFact attributionProcess)
        => inventoryProcess with
        {
            SoftwareId = attributionProcess.SoftwareId,
            SoftwareName = attributionProcess.SoftwareName,
            SoftwareKind = attributionProcess.SoftwareKind,
            SoftwareDisplayKind = attributionProcess.SoftwareDisplayKind,
            BaseScore = attributionProcess.BaseScore
        };

    private DatasetEntry GetOrCreate(SchedulingProcessMetricMask bit)
    {
        if (!datasets.TryGetValue(bit, out var dataset))
        {
            dataset = new DatasetEntry(bit);
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

    private static SchedulingProcessFact Project(
        SchedulingProcessFact process,
        SchedulingProcessMetricMask bit)
        => process with
        {
            ValidMetricMask = bit,
            CpuUsagePercent = bit == SchedulingProcessMetricMask.CpuUsage
                ? process.CpuUsagePercent
                : 0,
            MemoryUsagePercent = bit == SchedulingProcessMetricMask.MemoryUsage
                ? process.MemoryUsagePercent
                : 0,
            Gpus = bit is SchedulingProcessMetricMask.GpuUsage
                or SchedulingProcessMetricMask.GpuDedicatedMemory
                    ? process.Gpus
                        .Where(gpu => gpu.ValidMetricMask.HasFlag(bit))
                        .Select(gpu => gpu with
                        {
                            ValidMetricMask = bit,
                            UsagePercent = bit == SchedulingProcessMetricMask.GpuUsage
                                ? gpu.UsagePercent
                                : 0,
                            DedicatedMemoryUsedPercent =
                                bit == SchedulingProcessMetricMask.GpuDedicatedMemory
                                    ? gpu.DedicatedMemoryUsedPercent
                                    : 0,
                            UsageSourceGeneration =
                                bit == SchedulingProcessMetricMask.GpuUsage
                                    ? gpu.UsageSourceGeneration
                                    : 0,
                            DedicatedMemorySourceGeneration =
                                bit == SchedulingProcessMetricMask.GpuDedicatedMemory
                                    ? gpu.DedicatedMemorySourceGeneration
                                    : 0,
                            UsageTopologyGeneration =
                                bit == SchedulingProcessMetricMask.GpuUsage
                                    ? gpu.UsageTopologyGeneration
                                    : 0,
                            DedicatedMemoryTopologyGeneration =
                                bit == SchedulingProcessMetricMask.GpuDedicatedMemory
                                    ? gpu.DedicatedMemoryTopologyGeneration
                                    : 0
                        })
                        .ToArray()
                    : [],
            RuntimeState = bit == SchedulingProcessMetricMask.RuntimeState
                ? process.RuntimeState
                : HostManagerRuntimeStates.BackgroundProcess,
            ForegroundFocused = bit == SchedulingProcessMetricMask.RuntimeState
                && process.ForegroundFocused,
            HasVisibleWindow = bit == SchedulingProcessMetricMask.RuntimeState
                && process.HasVisibleWindow,
            HasBackgroundWindow = bit == SchedulingProcessMetricMask.RuntimeState
                && process.HasBackgroundWindow,
            HasHiddenWindow = bit == SchedulingProcessMetricMask.RuntimeState
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
            BaseScore = 0,
            ValidMetricMask = SchedulingProcessMetricMask.None,
            CpuUsagePercent = 0,
            MemoryUsagePercent = 0,
            Gpus = [],
            RuntimeState = HostManagerRuntimeStates.BackgroundProcess,
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
            MemoryUsagePercent = 0,
            Gpus = [],
            RuntimeState = HostManagerRuntimeStates.BackgroundProcess,
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

    private sealed class DatasetEntry(SchedulingProcessMetricMask bit)
    {
        public SchedulingProcessMetricMask Bit { get; } = bit;
        public Dictionary<(int ProcessId, ulong ProcessStartKey), SchedulingProcessFact>
            Processes { get; set; } = [];
        public SamplingObservationStatus Status { get; set; } =
            SamplingObservationStatus.Unavailable;
        public ulong SourceGeneration { get; set; }
        public long ObservedAtUtcTicks { get; set; }
        public ulong InventoryGeneration { get; set; }
        public long InventoryObservedAtUtcTicks { get; set; }
        public uint EnumeratedCount { get; set; }
        public uint ExcludedCount { get; set; }
        public uint SkippedCount { get; set; }
        public uint OverflowCount { get; set; }
        public ulong TopologyGeneration { get; set; }
        public ulong TopologyFingerprint { get; set; }
        public SystemMemoryUsageDependency? MemoryUsageDependency { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public DateTimeOffset? ReadyUntil { get; set; }
        public string? FailureCode { get; set; }
        public string? FailureMessage { get; set; }

        public DatasetEntry Clone()
        {
            return new DatasetEntry(Bit)
            {
                Processes = new Dictionary<
                    (int ProcessId, ulong ProcessStartKey),
                    SchedulingProcessFact>(Processes),
                Status = Status,
                SourceGeneration = SourceGeneration,
                ObservedAtUtcTicks = ObservedAtUtcTicks,
                InventoryGeneration = InventoryGeneration,
                InventoryObservedAtUtcTicks = InventoryObservedAtUtcTicks,
                EnumeratedCount = EnumeratedCount,
                ExcludedCount = ExcludedCount,
                SkippedCount = SkippedCount,
                OverflowCount = OverflowCount,
                TopologyGeneration = TopologyGeneration,
                TopologyFingerprint = TopologyFingerprint,
                MemoryUsageDependency = MemoryUsageDependency,
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
                TopologyFingerprint = TopologyFingerprint,
                MemoryUsageDependency = MemoryUsageDependency
            };
    }

    private sealed class FoundationEntry(string datasetId)
    {
        public System.Collections.Immutable.ImmutableArray<SoftwareBaseScore> SoftwareBaseScores
            { get; set; } = [];
        public string DatasetId { get; } = datasetId;
        public Dictionary<
            (int ProcessId, ulong ProcessStartKey),
            SchedulingProcessFact> Processes { get; set; } = [];
        public SamplingObservationStatus Status { get; set; } =
            SamplingObservationStatus.Unavailable;
        public ulong SourceGeneration { get; set; }
        public long ObservedAtUtcTicks { get; set; }
        public uint EnumeratedCount { get; set; }
        public uint ExcludedCount { get; set; }
        public uint SkippedCount { get; set; }
        public uint OverflowCount { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public DateTimeOffset? ReadyUntil { get; set; }
        public string? FailureCode { get; set; }
        public string? FailureMessage { get; set; }

        public FoundationEntry Clone()
            => new(DatasetId)
            {
                SoftwareBaseScores = SoftwareBaseScores,
                Processes = new Dictionary<
                    (int ProcessId, ulong ProcessStartKey),
                    SchedulingProcessFact>(Processes),
                Status = Status,
                SourceGeneration = SourceGeneration,
                ObservedAtUtcTicks = ObservedAtUtcTicks,
                EnumeratedCount = EnumeratedCount,
                ExcludedCount = ExcludedCount,
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
        private double memoryUsagePercent;
        private string runtimeState = HostManagerRuntimeStates.BackgroundProcess;
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
                memoryUsagePercent = process.MemoryUsagePercent;
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
                        gpu.ValidMetricMask.HasFlag(
                            SchedulingProcessMetricMask.GpuDedicatedMemory)
                            ? gpu.DedicatedMemorySourceGeneration
                            : current.DedicatedMemorySourceGeneration,
                    UsageTopologyGeneration = gpu.ValidMetricMask.HasFlag(
                        SchedulingProcessMetricMask.GpuUsage)
                            ? gpu.UsageTopologyGeneration
                            : current.UsageTopologyGeneration,
                    DedicatedMemoryTopologyGeneration =
                        gpu.ValidMetricMask.HasFlag(
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
                MemoryUsagePercent = memoryUsagePercent,
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
