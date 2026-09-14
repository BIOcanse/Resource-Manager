using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Domain.ResourceBreakdown;

[Flags]
public enum SchedulingProcessMetricMask : ulong
{
    None = 0,
    CpuUsage = 1UL << 0,
    MemoryUsage = 1UL << 1,
    GpuUsage = 1UL << 2,
    GpuDedicatedMemory = 1UL << 3,
    RuntimeState = 1UL << 4
}

public sealed record SchedulingProcessGpuFact(
    int GpuIndex,
    ulong AdapterKey,
    SchedulingProcessMetricMask ValidMetricMask,
    double UsagePercent,
    double DedicatedMemoryUsedPercent,
    ulong UsageSourceGeneration,
    ulong DedicatedMemorySourceGeneration,
    ulong UsageTopologyGeneration = 0,
    ulong DedicatedMemoryTopologyGeneration = 0)
{
    public double? PrivateMemoryBytes { get; init; }
    public double? SharedMemoryBytes { get; init; }
    public double? AllocatedMemoryBytes => PrivateMemoryBytes + SharedMemoryBytes;
}

public sealed record SchedulingProcessDatasetObservation(
    SchedulingProcessMetricMask Metric,
    string DatasetId,
    SamplingObservationStatus Status,
    ulong SourceGeneration,
    long ObservedAtUtcTicks,
    ulong InventoryGeneration,
    long InventoryObservedAtUtcTicks)
{
    public long LastAttemptAtUtcTicks { get; init; }

    public long LastSuccessAtUtcTicks { get; init; }

    public long ReadyUntilUtcTicks { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }

    public ulong TopologyGeneration { get; init; }

    public ulong TopologyFingerprint { get; init; }

    public SystemMemoryUsageDependency? MemoryUsageDependency { get; init; }

    public static SchedulingProcessDatasetObservation CreateCurrent(
        SchedulingProcessMetricMask metric,
        ulong sourceGeneration,
        long observedAtUtcTicks,
        ulong inventoryGeneration,
        long inventoryObservedAtUtcTicks,
        long readyUntilUtcTicks = long.MaxValue,
        ulong topologyGeneration = 0,
        ulong topologyFingerprint = 0,
        SystemMemoryUsageDependency? memoryUsageDependency = null)
    {
        var memoryMetric = metric == SchedulingProcessMetricMask.MemoryUsage;
        if (memoryMetric != (memoryUsageDependency is not null)
            || memoryUsageDependency is not null
                && !memoryUsageDependency.IsWellFormed())
        {
            throw new ArgumentException(
                "Current process-memory observations require one valid system-memory dependency, and other metrics cannot carry it.",
                nameof(memoryUsageDependency));
        }

        return new(
            metric,
            SchedulingProcessFactSnapshot.DatasetIdFor(metric),
            SamplingObservationStatus.Current,
            sourceGeneration,
            observedAtUtcTicks,
            inventoryGeneration,
            inventoryObservedAtUtcTicks)
        {
            LastAttemptAtUtcTicks = observedAtUtcTicks,
            LastSuccessAtUtcTicks = observedAtUtcTicks,
            ReadyUntilUtcTicks = readyUntilUtcTicks,
            TopologyGeneration = topologyGeneration,
            TopologyFingerprint = topologyFingerprint,
            MemoryUsageDependency = memoryUsageDependency
        };
    }
}

public sealed record SchedulingProcessFoundationDatasetObservation(
    string DatasetId,
    SamplingObservationStatus Status,
    ulong SourceGeneration,
    long ObservedAtUtcTicks)
{
    public long LastAttemptAtUtcTicks { get; init; }

    public long LastSuccessAtUtcTicks { get; init; }

    public long ReadyUntilUtcTicks { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }

    public static SchedulingProcessFoundationDatasetObservation CreateCurrent(
        string datasetId,
        ulong sourceGeneration,
        long observedAtUtcTicks,
        long readyUntilUtcTicks = long.MaxValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        return new(
            datasetId.Trim(),
            SamplingObservationStatus.Current,
            sourceGeneration,
            observedAtUtcTicks)
        {
            LastAttemptAtUtcTicks = observedAtUtcTicks,
            LastSuccessAtUtcTicks = observedAtUtcTicks,
            ReadyUntilUtcTicks = readyUntilUtcTicks
        };
    }
}

public sealed record SchedulingProcessFact(
    int ProcessId,
    ulong ProcessStartKey,
    string ProcessName,
    string? ExecutablePath,
    string SoftwareId,
    string SoftwareName,
    string SoftwareKind,
    string SoftwareDisplayKind,
    double BaseScore,
    SchedulingProcessMetricMask ValidMetricMask,
    double CpuUsagePercent,
    double MemoryUsagePercent,
    ulong SourceGeneration,
    IReadOnlyList<SchedulingProcessGpuFact> Gpus)
{
    public string RuntimeState { get; init; } =
        HostManagerRuntimeStates.BackgroundProcess;

    public bool ForegroundFocused { get; init; }

    public bool HasVisibleWindow { get; init; }

    public bool HasBackgroundWindow { get; init; }

    public bool HasHiddenWindow { get; init; }
}

public sealed record SchedulingProcessFactRequest(
    SchedulingProcessMetricMask RequestedMetricMask,
    SystemMemoryUsageDependency? ExpectedMemoryUsageDependency,
    SchedulingGpuInventorySnapshot GpuInventory);

public readonly record struct SoftwareBaseScore(string SoftwareId, double BaseScore);

public sealed record SchedulingProcessFactSnapshot(
    SamplingObservationStatus InventoryStatus,
    ulong Generation,
    long ObservedAtUtcTicks,
    uint EnumeratedCount,
    uint EmittedCount,
    uint SkippedCount,
    uint OverflowCount,
    SchedulingProcessMetricMask RequestedMetricMask,
    SchedulingProcessMetricMask CurrentMetricMask,
    IReadOnlyList<SchedulingProcessFact> Processes,
    ulong GpuSourceGeneration = 0,
    long GpuObservedAtUtcTicks = 0,
    ulong GpuTopologyGeneration = 0,
    ulong GpuTopologyFingerprint = 0,
    uint ExcludedCount = 0)
{
    public bool HasSeparateFoundationPayloads { get; init; }

    public IReadOnlyList<SchedulingProcessFact> InventoryProcesses { get; init; } = [];

    public IReadOnlyList<SchedulingProcessFact> AttributionProcesses { get; init; } = [];

    public System.Collections.Immutable.ImmutableArray<SoftwareBaseScore> SoftwareBaseScores
        { get; init; } = [];

    public IReadOnlyDictionary<
        string,
        SchedulingProcessFoundationDatasetObservation> FoundationDatasetObservations
        { get; init; } = new Dictionary<
            string,
            SchedulingProcessFoundationDatasetObservation>(
                StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<
        SchedulingProcessMetricMask,
        SchedulingProcessDatasetObservation> DatasetObservations { get; init; } =
            new Dictionary<
                SchedulingProcessMetricMask,
                SchedulingProcessDatasetObservation>();

    public bool TryGetCurrentFoundationDataset(
        string datasetId,
        out SchedulingProcessFoundationDatasetObservation observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        if (!FoundationDatasetObservations.TryGetValue(
                datasetId,
                out observation!)
            || !string.Equals(
                observation.DatasetId,
                datasetId,
                StringComparison.OrdinalIgnoreCase)
            || observation.Status != SamplingObservationStatus.Current
            || observation.SourceGeneration == 0
            || observation.ObservedAtUtcTicks <= 0
            || observation.LastAttemptAtUtcTicks <= 0
            || observation.LastSuccessAtUtcTicks <= 0
            || observation.FailureCode is not null)
        {
            observation = null!;
            return false;
        }
        return true;
    }

    public bool TryGetCurrentDataset(
        SchedulingProcessMetricMask metric,
        out SchedulingProcessDatasetObservation observation)
    {
        if (!IsSingleMetric(metric)
            || !DatasetObservations.TryGetValue(metric, out observation!)
            || observation.Metric != metric
            || !string.Equals(
                observation.DatasetId,
                DatasetIdFor(metric),
                StringComparison.OrdinalIgnoreCase)
            || observation.Status != SamplingObservationStatus.Current
            || observation.SourceGeneration == 0
            || observation.ObservedAtUtcTicks <= 0
            || observation.InventoryGeneration == 0
            || observation.InventoryObservedAtUtcTicks <= 0
            || observation.LastAttemptAtUtcTicks <= 0
            || observation.LastSuccessAtUtcTicks <= 0
            || observation.FailureCode is not null)
        {
            observation = null!;
            return false;
        }

        var gpuMetric = metric is SchedulingProcessMetricMask.GpuUsage
            or SchedulingProcessMetricMask.GpuDedicatedMemory;
        if (gpuMetric != (observation.TopologyGeneration > 0
                && observation.TopologyFingerprint > 0))
        {
            observation = null!;
            return false;
        }

        var memoryMetric = metric == SchedulingProcessMetricMask.MemoryUsage;
        if (memoryMetric != (observation.MemoryUsageDependency is not null)
            || observation.MemoryUsageDependency is not null
                && !observation.MemoryUsageDependency.IsWellFormed())
        {
            observation = null!;
            return false;
        }

        return true;
    }

    public bool IsCurrentComplete()
    {
        if (!IsInventoryCurrentComplete()
            || (CurrentMetricMask & RequestedMetricMask) != RequestedMetricMask
            || EnumerateMetrics(RequestedMetricMask).Any(metric =>
                !TryGetCurrentDataset(metric, out _)))
        {
            return false;
        }

        foreach (var process in Processes)
        {
            if (process.ValidMetricMask == SchedulingProcessMetricMask.None
                || (process.ValidMetricMask & ~RequestedMetricMask)
                    != SchedulingProcessMetricMask.None
                || !IsPercentValid(process.CpuUsagePercent, process.ValidMetricMask, SchedulingProcessMetricMask.CpuUsage)
                || !IsPercentValid(process.MemoryUsagePercent, process.ValidMetricMask, SchedulingProcessMetricMask.MemoryUsage)
                || process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.RuntimeState)
                    && !IsRuntimeStateValid(process)
                || !AreGpuFactsValid(process))
            {
                return false;
            }
        }

        return true;
    }

    public bool IsInventoryCurrentComplete()
    {
        if (InventoryStatus != SamplingObservationStatus.Current
            || Generation == 0
            || ObservedAtUtcTicks <= 0
            || OverflowCount != 0
            || EmittedCount != Processes.Count
            || (ulong)EnumeratedCount !=
                (ulong)EmittedCount + ExcludedCount + SkippedCount)
        {
            return false;
        }

        var identities = new HashSet<(int ProcessId, ulong ProcessStartKey)>();
        foreach (var process in Processes)
        {
            if (process.ProcessId <= 0
                || process.ProcessStartKey == 0
                || process.SourceGeneration != Generation
                || string.IsNullOrWhiteSpace(process.ProcessName)
                || string.IsNullOrWhiteSpace(process.SoftwareId)
                || !double.IsFinite(process.BaseScore)
                || process.BaseScore < 0
                || !identities.Add((process.ProcessId, process.ProcessStartKey)))
            {
                return false;
            }
        }

        return true;
    }

    public bool IsCpuCurrentComplete()
        => IsScalarMetricCurrentComplete(SchedulingProcessMetricMask.CpuUsage);

    public bool IsMemoryCurrentComplete()
        => IsScalarMetricCurrentComplete(SchedulingProcessMetricMask.MemoryUsage);

    public bool IsMemoryCurrentComplete(
        SystemMemoryUsageDependency expectedDependency)
        => expectedDependency is not null
            && expectedDependency.IsWellFormed()
            && IsMemoryCurrentComplete()
            && TryGetCurrentDataset(
                SchedulingProcessMetricMask.MemoryUsage,
                out var observation)
            && observation.MemoryUsageDependency == expectedDependency;

    public bool IsRuntimeStateCurrentComplete()
    {
        if (!IsInventoryCurrentComplete()
            || !RequestedMetricMask.HasFlag(SchedulingProcessMetricMask.RuntimeState)
            || !CurrentMetricMask.HasFlag(SchedulingProcessMetricMask.RuntimeState)
            || !TryGetCurrentDataset(
                SchedulingProcessMetricMask.RuntimeState,
                out _))
        {
            return false;
        }

        return Processes
            .Where(static process => process.ValidMetricMask.HasFlag(
                SchedulingProcessMetricMask.RuntimeState))
            .All(IsRuntimeStateValid);
    }

    public bool IsGpuUsageCurrentComplete(SchedulingGpuInventorySnapshot inventory)
        => IsGpuMetricCurrentComplete(
            inventory,
            SchedulingProcessMetricMask.GpuUsage,
            static adapter => adapter.UsageStatus == SamplingObservationStatus.Current);

    public bool IsGpuDedicatedMemoryCurrentComplete(SchedulingGpuInventorySnapshot inventory)
        => IsGpuMetricCurrentComplete(
            inventory,
            SchedulingProcessMetricMask.GpuDedicatedMemory,
            static adapter => adapter.CapabilityMask.HasFlag(SchedulingGpuCapabilityMask.DedicatedMemory)
                && adapter.CapacityStatus == SamplingObservationStatus.Current);

    private bool IsScalarMetricCurrentComplete(SchedulingProcessMetricMask metric)
    {
        if (!IsInventoryCurrentComplete()
            || !RequestedMetricMask.HasFlag(metric)
            || !CurrentMetricMask.HasFlag(metric)
            || !TryGetCurrentDataset(metric, out _))
        {
            return false;
        }

        return Processes
            .Where(process => process.ValidMetricMask.HasFlag(metric))
            .All(process => IsPercentValid(
                metric == SchedulingProcessMetricMask.CpuUsage
                    ? process.CpuUsagePercent
                    : process.MemoryUsagePercent,
                process.ValidMetricMask,
                metric));
    }

    private bool IsGpuMetricCurrentComplete(
        SchedulingGpuInventorySnapshot inventory,
        SchedulingProcessMetricMask metric,
        Func<SchedulingGpuAdapterObservation, bool> isRequiredAdapter)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (!IsInventoryCurrentComplete()
            || !inventory.IsCurrentComplete()
            || !RequestedMetricMask.HasFlag(metric)
            || !CurrentMetricMask.HasFlag(metric)
            || !TryGetCurrentDataset(metric, out var dataset)
            || dataset.TopologyGeneration != inventory.Generation
            || dataset.TopologyFingerprint != inventory.TopologyFingerprint)
        {
            return false;
        }

        var adapters = inventory.Adapters.Where(isRequiredAdapter).ToArray();

        foreach (var process in Processes.Where(process =>
                     process.ValidMetricMask.HasFlag(metric)))
        {
            if (!AreGpuFactsValid(process))
            {
                return false;
            }

            var found = false;
            foreach (var gpu in process.Gpus.Where(gpu =>
                         gpu.ValidMetricMask.HasFlag(metric)))
            {
                found = true;
                if (!adapters.Any(adapter =>
                        adapter.AdapterKey == gpu.AdapterKey
                        && adapter.Index == gpu.GpuIndex)
                    || SourceGenerationFor(gpu, metric) != dataset.SourceGeneration
                    || TopologyGenerationFor(gpu, metric)
                        != dataset.TopologyGeneration)
                {
                    return false;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private bool AreGpuFactsValid(SchedulingProcessFact process)
    {
        var indexes = new HashSet<int>();
        var keys = new HashSet<ulong>();
        foreach (var gpu in process.Gpus)
        {
            if (gpu.GpuIndex < 0
                || gpu.AdapterKey == 0
                || !indexes.Add(gpu.GpuIndex)
                || !keys.Add(gpu.AdapterKey)
                || !IsGpuLineageValid(
                    gpu,
                    SchedulingProcessMetricMask.GpuUsage)
                || !IsGpuLineageValid(
                    gpu,
                    SchedulingProcessMetricMask.GpuDedicatedMemory)
                || !IsPercentValid(gpu.UsagePercent, gpu.ValidMetricMask, SchedulingProcessMetricMask.GpuUsage)
                || !IsPercentValid(
                    gpu.DedicatedMemoryUsedPercent,
                    gpu.ValidMetricMask,
                    SchedulingProcessMetricMask.GpuDedicatedMemory))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsGpuLineageValid(
        SchedulingProcessGpuFact gpu,
        SchedulingProcessMetricMask metric)
    {
        var present = gpu.ValidMetricMask.HasFlag(metric);
        var sourceGeneration = SourceGenerationFor(gpu, metric);
        var topologyGeneration = TopologyGenerationFor(gpu, metric);
        if (!present)
        {
            return sourceGeneration == 0 && topologyGeneration == 0;
        }

        return sourceGeneration > 0
            && topologyGeneration > 0
            && (!DatasetObservations.TryGetValue(metric, out var observation)
                || sourceGeneration == observation.SourceGeneration
                    && topologyGeneration == observation.TopologyGeneration);
    }

    private static ulong SourceGenerationFor(
        SchedulingProcessGpuFact gpu,
        SchedulingProcessMetricMask metric)
        => metric switch
        {
            SchedulingProcessMetricMask.GpuUsage => gpu.UsageSourceGeneration,
            SchedulingProcessMetricMask.GpuDedicatedMemory =>
                gpu.DedicatedMemorySourceGeneration,
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };

    private static ulong TopologyGenerationFor(
        SchedulingProcessGpuFact gpu,
        SchedulingProcessMetricMask metric)
        => metric switch
        {
            SchedulingProcessMetricMask.GpuUsage => gpu.UsageTopologyGeneration,
            SchedulingProcessMetricMask.GpuDedicatedMemory =>
                gpu.DedicatedMemoryTopologyGeneration,
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };

    internal static string DatasetIdFor(SchedulingProcessMetricMask metric)
        => metric switch
        {
            SchedulingProcessMetricMask.CpuUsage =>
                SamplingDatasetIds.ProcessCpuUsage,
            SchedulingProcessMetricMask.MemoryUsage =>
                SamplingDatasetIds.ProcessMemoryUsage,
            SchedulingProcessMetricMask.GpuUsage => SamplingDatasetIds.ProcessGpuUsage,
            SchedulingProcessMetricMask.GpuDedicatedMemory =>
                SamplingDatasetIds.ProcessGpuVram,
            SchedulingProcessMetricMask.RuntimeState =>
                SamplingDatasetIds.ProcessRuntimeState,
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };

    private static bool IsSingleMetric(SchedulingProcessMetricMask metric)
        => metric != SchedulingProcessMetricMask.None
            && ((ulong)metric & ((ulong)metric - 1)) == 0
            && metric <= SchedulingProcessMetricMask.RuntimeState;

    private static IEnumerable<SchedulingProcessMetricMask> EnumerateMetrics(
        SchedulingProcessMetricMask mask)
    {
        foreach (var metric in new[]
                 {
                     SchedulingProcessMetricMask.CpuUsage,
                     SchedulingProcessMetricMask.MemoryUsage,
                     SchedulingProcessMetricMask.GpuUsage,
                     SchedulingProcessMetricMask.GpuDedicatedMemory,
                     SchedulingProcessMetricMask.RuntimeState
                 })
        {
            if (mask.HasFlag(metric))
            {
                yield return metric;
            }
        }
    }

    private static bool IsPercentValid(
        double value,
        SchedulingProcessMetricMask validMask,
        SchedulingProcessMetricMask bit)
    {
        return !validMask.HasFlag(bit)
            || double.IsFinite(value) && value >= 0 && value <= 100;
    }

    private static bool IsRuntimeStateValid(SchedulingProcessFact process)
    {
        if (process.RuntimeState is not (
                HostManagerRuntimeStates.ForegroundFocused
                or HostManagerRuntimeStates.ForegroundUnfocused
                or HostManagerRuntimeStates.BackgroundWindow
                or HostManagerRuntimeStates.TrayOnly
                or HostManagerRuntimeStates.BackgroundProcess))
        {
            return false;
        }

        return process.RuntimeState switch
        {
            HostManagerRuntimeStates.ForegroundFocused =>
                process.ForegroundFocused && process.HasVisibleWindow,
            HostManagerRuntimeStates.ForegroundUnfocused =>
                !process.ForegroundFocused && process.HasVisibleWindow,
            HostManagerRuntimeStates.BackgroundWindow =>
                !process.ForegroundFocused
                && !process.HasVisibleWindow
                && process.HasBackgroundWindow,
            HostManagerRuntimeStates.TrayOnly =>
                !process.ForegroundFocused
                && !process.HasVisibleWindow
                && !process.HasBackgroundWindow
                && process.HasHiddenWindow,
            HostManagerRuntimeStates.BackgroundProcess =>
                !process.ForegroundFocused
                && !process.HasVisibleWindow
                && !process.HasBackgroundWindow
                && !process.HasHiddenWindow,
            _ => false
        };
    }
}
