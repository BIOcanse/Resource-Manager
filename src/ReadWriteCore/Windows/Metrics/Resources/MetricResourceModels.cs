using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed record StorageSensorSnapshot(HardwareSensorProviderState ProviderState, IReadOnlyList<DiskTemperatureSensor> Disks)
{
    public IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity>? Identities { get; init; }
}
public sealed record NotebookOemFanSensorSnapshot(HardwareSensorProviderState ProviderState,
    double? CpuFanSpeedRpm, double? CpuFanSpeedPercent, IReadOnlyList<PlatformGpuSensor> GpuFanSensors)
{
    public IReadOnlyList<NotebookOemPhysicalFanReading> PhysicalFans { get; init; } = [];
}
public enum NotebookOemPhysicalFanRole { Cpu, DiscreteGpu }
public sealed record NotebookOemPhysicalFanReading(string IdentityKey, NotebookOemPhysicalFanRole Role,
    double? SpeedRpm, double? ReportedPwmDutyPercent);
public sealed record NetworkInterfaceTopologyRead(NativeMetricSnapshotCatalogTopologyStatus Status,
    IReadOnlyList<NetworkInterfaceCatalogIdentity> Identities);
public sealed record NetworkInterfaceMetricRead(string DeviceIdentity,
    IReadOnlyDictionary<MetricFieldCode, ulong> Counters,
    IReadOnlyDictionary<MetricFieldCode, NativeMetricSnapshotTimedCounter> Rates, bool RequiresTopologyRefresh = false);
public sealed record NumaMemoryTopologyRead(NativeMetricSnapshotCatalogTopologyStatus Status,
    IReadOnlyList<NumaMemoryCatalogIdentity> Identities);
public readonly record struct CpuFrequency(int CurrentMhz, int MaxMhz, double Percent, string Source);
public sealed record MechrevoUwAcpiSensorSnapshot(
    HardwareSensorProviderState ProviderState,
    double? CpuFanSpeedRpm,
    double? CpuFanSpeedPercent,
    PlatformGpuSensor? GpuFanSensor);

