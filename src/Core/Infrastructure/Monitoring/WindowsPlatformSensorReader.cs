using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Devices;
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
            HardwareSensorProviderStates.NotRequested,
            "当前快照未请求内存温度、主板传感、磁盘温度或系统风扇。");
        return new WindowsPlatformSensorSnapshot(state, state, state, state, null, null, null, null, null, null, null, [], [], []);
    }

    internal StorageSensorSnapshot ReadStorageSensors()
        => storageZone.ReadSensors();

    internal StorageSensorSnapshot ReadNvmeSensors(StorageSensorReadRequest request)
        => storageZone.ReadNvmeSensors(request);

    internal StorageSensorSnapshot ReadAtaSensors(AtaSmartSensorReadRequest request)
        => storageZone.ReadAtaSensors(request);

    internal Task<WindowsDiskPerformanceCaptureResult> ReadDiskPerformanceAsync(
        IReadOnlyList<WindowsDiskPerformanceTarget> targets, CancellationToken token)
        => storageZone.ReadDiskPerformanceAsync(targets, token);

    internal NativeMetricSnapshotOrdinalTopologyRead ReadHardwareMonitorFanIdentities(ulong generation)
        => hardwareMonitorZone.ReadFanCatalog(generation);

    internal HardwareMonitorSensorSnapshot ReadHardwareMonitorSensors()
        => hardwareMonitorZone.ReadSensors();

    internal NotebookOemFanSensorSnapshot ReadNotebookOemFanSensors()
        => notebookOemFanZone.ReadSensors();

    internal void SynchronizeNotebookOemFanDemand(bool hasDemand)
        => notebookOemFanZone.SynchronizeDemand(hasDemand);

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
