using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed record HardwareMonitorSensorSnapshot(
    HardwareSensorProviderState ProviderState,
    double? MemoryTemperatureCelsius,
    PlatformSensorReading? MotherboardTemperature,
    PlatformSensorReading? VrmTemperature,
    PlatformSensorReading? ChipsetTemperature,
    PlatformSensorReading? MotherboardVoltage,
    double? CpuFanSpeedRpm,
    double? CpuFanSpeedPercent,
    IReadOnlyList<SystemFanSensor> SystemFans,
    IReadOnlyList<PlatformGpuSensor> GpuSensors);

internal sealed record HardwareMonitorHardwareInfo(
    string Identifier,
    string Name,
    string HardwareType);
