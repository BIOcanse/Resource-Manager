using System.Collections.Immutable;
using ResourceManager.App.Domain.Monitoring;

namespace ResourceManager.App.Domain.Metrics;

public sealed record HardwareMetricSnapshot(
    DateTimeOffset CapturedAt,
    CpuMetrics Cpu,
    MemoryMetrics Memory,
    VirtualMemoryMetrics VirtualMemory,
    IReadOnlyList<GpuMetrics> Gpus,
    SchedulingGpuInventorySnapshot GpuInventory,
    IReadOnlyDictionary<string, MetricReadingValue> Items)
{
    // Collection-to-current commands; ordinary reads/failures never populate this.
    internal ImmutableHashSet<string> InvalidatedDatasetIds { get; init; } = ImmutableHashSet<string>.Empty;

    public IReadOnlyDictionary<string, MetricDeviceFieldBinding> FieldBindings { get; init; } =
        new Dictionary<string, MetricDeviceFieldBinding>();
    [System.Text.Json.Serialization.JsonIgnore]
    public ImmutableDictionary<string, ImmutableArray<HardwareMetricHistorySample>> History { get; init; }
        = ImmutableDictionary<string, ImmutableArray<HardwareMetricHistorySample>>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, HardwareMetricDatasetObservation>
        Datasets { get; init; } =
            new Dictionary<string, HardwareMetricDatasetObservation>(
                StringComparer.OrdinalIgnoreCase);

    public ulong WorkspaceIdentity { get; init; }

    public ulong ConfigurationGeneration { get; init; }

    public ulong CatalogGeneration { get; init; }

    public ulong CommittedGeneration { get; init; }

    public bool TryGetCurrentDataset(
        string datasetId,
        out HardwareMetricDatasetObservation observation)
        => TryGetCurrentDataset(
            datasetId,
            DateTimeOffset.UtcNow,
            out observation);

    public bool TryGetCurrentDataset(
        string datasetId,
        DateTimeOffset now,
        out HardwareMetricDatasetObservation observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        _ = now;
        if (Datasets.TryGetValue(datasetId, out observation!)
            && string.Equals(
                observation.DatasetId,
                datasetId,
                StringComparison.OrdinalIgnoreCase)
            && observation.Status == SamplingObservationStatus.Current
            && observation.FailureCode is null)
        {
            return true;
        }

        observation = null!;
        return false;
    }
}

public sealed record HardwareMetricDatasetObservation(
    string DatasetId,
    SamplingObservationStatus Status,
    ulong SourceGeneration,
    long ObservedAtUtcTicks,
    ulong WorkspaceIdentity,
    ulong ConfigurationGeneration,
    ulong CatalogGeneration,
    ulong CommittedGeneration)
{
    public long LastAttemptAtUtcTicks { get; init; }

    public long LastSuccessAtUtcTicks { get; init; }

    public long ReadyUntilUtcTicks { get; init; }

    public string? FailureCode { get; init; }
}

public sealed record CpuMetrics(
    string Name,
    double UsagePercent,
    bool IsUsageAvailable,
    CpuMetricObservationStatus ObservationStatus,
    ulong SourceGeneration,
    long SampleDurationMilliseconds,
    int CurrentFrequencyMhz,
    int MaxFrequencyMhz,
    double FrequencyPercent,
    string FrequencySource,
    CpuSensorMetrics Sensors);

public enum CpuMetricObservationStatus : uint
{
    NotRequested = 0,
    Warming = 1,
    Complete = 2,
    Unavailable = 3
}

public sealed record MemoryMetrics(
    ulong UsedBytes,
    ulong TotalBytes,
    double UsagePercent,
    bool IsUsageAvailable)
{
    public ulong? AvailableBytes { get; init; }
    internal SamplingObservationStatus ObservationStatus { get; init; } =
        SamplingObservationStatus.Invalid;
}

public sealed record VirtualMemoryMetrics(
    ulong UsedBytes,
    ulong TotalBytes,
    double UsagePercent,
    bool IsSelectable)
{
    public ulong? CommitPeakBytes { get; init; }
    public ulong? SystemCacheBytes { get; init; }
    public ulong? KernelPagedBytes { get; init; }
    public ulong? KernelNonpagedBytes { get; init; }
    public uint? ProcessCount { get; init; }
    public uint? ThreadCount { get; init; }
    public uint? HandleCount { get; init; }
    internal SamplingObservationStatus ObservationStatus { get; init; } =
        SamplingObservationStatus.Invalid;
}

