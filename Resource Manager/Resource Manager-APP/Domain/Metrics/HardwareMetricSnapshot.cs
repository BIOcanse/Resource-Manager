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
    IReadOnlyDictionary<string, MetricValue> Items)
{
    internal ImmutableDictionary<string, ImmutableArray<HardwareMetricHistorySample>> History { get; init; }
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

public sealed record HardwareSensorProviderState(
    string Provider,
    string State,
    string? Message);

public sealed record CpuSensorMetrics(
    HardwareSensorProviderState ProviderState,
    double? PackagePowerWatts,
    double? CoreVoltageVolts,
    double? PackageCurrentAmps,
    double? TemperatureCelsius,
    double? StapmPowerWatts = null,
    double? ActualPowerWatts = null,
    double? AveragePowerWatts = null,
    double? TdcCurrentAmps = null,
    double? EdcCurrentAmps = null,
    double? SocPowerWatts = null,
    double? SocVoltageVolts = null,
    double? ApuFrequencyMhz = null,
    double? ApuVoltageVolts = null,
    double? ApuTemperatureCelsius = null,
    double? SmuFrequencyMhz = null);

public sealed record MemoryMetrics(
    ulong UsedBytes,
    ulong TotalBytes,
    double UsagePercent,
    bool IsUsageAvailable,
    string HardwareDescription)
{
    internal SamplingObservationStatus ObservationStatus { get; init; } =
        SamplingObservationStatus.Invalid;
}

public sealed record VirtualMemoryMetrics(
    ulong UsedBytes,
    ulong TotalBytes,
    double UsagePercent,
    string Detail,
    bool IsSelectable)
{
    internal SamplingObservationStatus ObservationStatus { get; init; } =
        SamplingObservationStatus.Invalid;
}

public sealed record GpuMetrics(
    int Index,
    string Name,
    double UsagePercent,
    int GraphicsFrequencyMhz,
    int StandardGraphicsFrequencyMhz,
    double GraphicsFrequencyPercent,
    int MemoryFrequencyMhz,
    ulong UsedMemoryBytes,
    ulong TotalMemoryBytes,
    double MemoryUsagePercent,
    GpuSensorMetrics Sensors,
    string UsageProvider = "",
    bool IsUsageAvailable = true,
    string? IdentityKey = null);

public sealed record GpuSensorMetrics(
    HardwareSensorProviderState ProviderState,
    double? PowerWatts,
    double? PowerLimitWatts,
    double? BoardPowerWatts,
    double? TemperatureCelsius,
    double? HotspotTemperatureCelsius,
    double? IntakeTemperatureCelsius,
    double? FanSpeedPercent,
    double? FanSpeedRpm,
    double? CoreVoltageVolts,
    double? CurrentAmps);
