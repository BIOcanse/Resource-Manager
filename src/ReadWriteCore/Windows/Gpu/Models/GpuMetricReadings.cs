using ResourceManager.App.Domain.Metrics;
namespace ResourceManager.App.Domain.Metrics;

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
    string? IdentityKey = null)
{
    /// <summary>Current catalog LUID routing key; neither persistent device identity nor telemetry.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ulong? AdapterKey { get; init; }

    // Provider transport only; committed observations are published through Items/Datasets.
    public IReadOnlyDictionary<MetricFieldCode, double?> AdditionalReadings { get; init; } =
        new Dictionary<MetricFieldCode, double?>();
    public IReadOnlyDictionary<MetricFieldCode, ulong> AdditionalIntegerReadings { get; init; } =
        new Dictionary<MetricFieldCode, ulong>();
    public IReadOnlyDictionary<MetricFieldCode, DateTimeOffset> AdditionalObservedAt { get; init; } =
        new Dictionary<MetricFieldCode, DateTimeOffset>();
}

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
