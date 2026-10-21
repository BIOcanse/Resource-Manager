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
    double? CpuTemperatureCelsius = null);

internal sealed record HardwareMonitorHardwareInfo(
    string Identifier,
    string Name,
    string HardwareType);


// Topology completeness describes query success, including an empty catalog.
public sealed record HardwareMonitorFanIdentityRead(bool Complete, IReadOnlyList<HardwareMonitorFanIdentity> Identities);
public sealed record HardwareMonitorFanIdentity(int Index, string IdentityKey);
