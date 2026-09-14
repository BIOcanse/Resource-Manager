using ResourceManager.App.Domain.Metrics;
using static ResourceManager.App.Infrastructure.Monitoring.PlatformSensorWmiUtilities;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed partial class WindowsPlatformSensorReader(
    WindowsStorageSensorsMonitoringZone storageZone,
    HardwareMonitorWmiMonitoringZone hardwareMonitorZone,
    NotebookOemFanMonitoringZone notebookOemFanZone)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    private readonly object gate = new();
    private WindowsPlatformSensorSnapshot? cachedSnapshot;
    private DateTimeOffset cachedAt;

    public WindowsPlatformSensorSnapshot Read()
    {
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (cachedSnapshot is not null && now - cachedAt < CacheDuration)
            {
                return cachedSnapshot;
            }
        }

        var refreshed = ReadUncached();
        lock (gate)
        {
            cachedSnapshot = refreshed;
            cachedAt = now;
        }

        return refreshed;
    }

    public static WindowsPlatformSensorSnapshot NotRequested()
    {
        var state = new HardwareSensorProviderState(
            "Windows platform sensors",
            "NotRequested",
            "当前快照未请求内存温度、主板传感、磁盘温度或系统风扇。");
        return new WindowsPlatformSensorSnapshot(state, state, state, state, null, null, null, null, null, null, null, [], [], []);
    }

    public bool CanReadAnySource()
    {
        return storageZone.CanRead
            || hardwareMonitorZone.CanRead
            || notebookOemFanZone.CanRead;
    }

    internal StorageSensorSnapshot ReadStorageSensors()
        => storageZone.ReadSensors();

    internal HardwareMonitorSensorSnapshot ReadHardwareMonitorSensors()
        => hardwareMonitorZone.ReadSensors();

    internal NotebookOemFanSensorSnapshot ReadNotebookOemFanSensors()
        => notebookOemFanZone.ReadSensors();

    private WindowsPlatformSensorSnapshot ReadUncached()
    {
        var storage = storageZone.ReadSensors();
        var hardwareMonitor = hardwareMonitorZone.ReadSensors();
        var notebookOem = notebookOemFanZone.ReadSensors();
        var fanProviderState = MergeProviderStates("Notebook fan providers", hardwareMonitor.ProviderState, notebookOem.ProviderState);
        var providerState = MergeProviderStates("Windows platform sensors", storage.ProviderState, hardwareMonitor.ProviderState, notebookOem.ProviderState);
        var gpuSensors = notebookOem.GpuFanSensors.Count == 0
            ? hardwareMonitor.GpuSensors
            : hardwareMonitor.GpuSensors.Concat(notebookOem.GpuFanSensors).ToArray();

        return new WindowsPlatformSensorSnapshot(
            providerState,
            storage.ProviderState,
            hardwareMonitor.ProviderState,
            fanProviderState,
            hardwareMonitor.MemoryTemperatureCelsius,
            hardwareMonitor.MotherboardTemperature,
            hardwareMonitor.VrmTemperature,
            hardwareMonitor.ChipsetTemperature,
            hardwareMonitor.MotherboardVoltage,
            hardwareMonitor.CpuFanSpeedRpm ?? notebookOem.CpuFanSpeedRpm,
            hardwareMonitor.CpuFanSpeedPercent ?? notebookOem.CpuFanSpeedPercent,
            storage.Disks,
            hardwareMonitor.SystemFans,
            gpuSensors);
    }
}
internal sealed record PlatformSensorReading(
    string Name,
    double? Value,
    string Detail);

internal sealed record WindowsPlatformSensorSnapshot(
    HardwareSensorProviderState ProviderState,
    HardwareSensorProviderState StorageProviderState,
    HardwareSensorProviderState HardwareMonitorProviderState,
    HardwareSensorProviderState FanProviderState,
    double? MemoryTemperatureCelsius,
    PlatformSensorReading? MotherboardTemperature,
    PlatformSensorReading? VrmTemperature,
    PlatformSensorReading? ChipsetTemperature,
    PlatformSensorReading? MotherboardVoltage,
    double? CpuFanSpeedRpm,
    double? CpuFanSpeedPercent,
    IReadOnlyList<DiskTemperatureSensor> Disks,
    IReadOnlyList<SystemFanSensor> SystemFans,
    IReadOnlyList<PlatformGpuSensor> GpuSensors);

internal sealed record DiskTemperatureSensor(
    int Index,
    string Name,
    double? TemperatureCelsius,
    string Detail);

internal sealed record SystemFanSensor(
    int Index,
    string Name,
    double? SpeedRpm,
    double? SpeedPercent,
    string Detail);

internal sealed record PlatformGpuSensor(
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
