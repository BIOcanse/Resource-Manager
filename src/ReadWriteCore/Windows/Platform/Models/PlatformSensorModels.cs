using ResourceManager.App.Domain.Metrics;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed record PlatformSensorReading(
    string Name,
    double? Value,
    string Detail);

public sealed record DiskTemperatureSensor(
    int Index,
    string Name,
    double? TemperatureCelsius,
    string Detail)
{
    public string IdentityKey { get; init; } = string.Empty;
    public IReadOnlyDictionary<MetricFieldCode, double> Values { get; init; }
        = new Dictionary<MetricFieldCode, double>();
    public IReadOnlyDictionary<MetricFieldCode, UInt128> WideIntegerValues { get; init; } = new Dictionary<MetricFieldCode, UInt128>();
    public IReadOnlyDictionary<MetricFieldCode, byte[]> BytesValues { get; init; } = new Dictionary<MetricFieldCode, byte[]>();
    public IReadOnlyDictionary<MetricFieldCode, ulong> IntegerValues { get; init; }
        = new Dictionary<MetricFieldCode, ulong>();
}

public sealed record SystemFanSensor(
    int Index,
    string Name,
    double? SpeedRpm,
    double? SpeedPercent,
    string Detail)
{
    public string IdentityKey { get; init; } = string.Empty;
}

public sealed record PlatformGpuSensor(
    int Index,
    string Name,
    string Detail,
    double? TemperatureCelsius,
    double? PowerWatts,
    double? FanSpeedRpm,
    double? FanSpeedPercent,
    double? CoreVoltageVolts,
    double? CurrentAmps)
{
    public bool HasAnyValue =>
        TemperatureCelsius is not null
        || PowerWatts is not null
        || FanSpeedRpm is not null
        || FanSpeedPercent is not null
        || CoreVoltageVolts is not null
        || CurrentAmps is not null;
}
