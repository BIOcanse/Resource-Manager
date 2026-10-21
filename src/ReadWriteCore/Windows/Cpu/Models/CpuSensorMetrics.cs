namespace ResourceManager.App.Domain.Metrics;

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

