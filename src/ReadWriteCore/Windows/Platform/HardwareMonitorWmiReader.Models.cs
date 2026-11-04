using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed record HardwareMonitorSensorSnapshot(
    HardwareSensorProviderState ProviderState,
    double? MemoryTemperatureCelsius,
    PlatformSensorReading? MotherboardTemperature,
    PlatformSensorReading? VrmTemperature,
    PlatformSensorReading? ChipsetTemperature,
    PlatformSensorReading? MotherboardVoltage,
    double? CpuFanSpeedRpm,
    double? CpuFanSpeedPercent,
    IReadOnlyList<SystemFanSensor> SystemFans,
    IReadOnlyList<PlatformGpuSensor> GpuSensors,
    double? CpuTemperatureCelsius = null)
{
    public string? CpuTemperatureSensorIdentifier { get; init; }
    public string? CpuTemperatureReadingKind { get; init; }
    public IReadOnlyList<string> SensorIdentifiers { get; init; } = [];
    public bool? CatalogComplete { get; init; }
}

internal sealed record HardwareMonitorHardwareInfo(
    string Identifier,
    string Name,
    string HardwareType);


// Topology completeness describes query success, including an empty catalog.
public sealed record HardwareMonitorFanIdentityRead(bool Complete, IReadOnlyList<HardwareMonitorFanIdentity> Identities);
public sealed record HardwareMonitorFanIdentity(int Index, string IdentityKey)
{
    public string? Provider { get; init; }
    public string? SensorIdentifier { get; init; }
    public string? NameDeclared { get; init; }
}
