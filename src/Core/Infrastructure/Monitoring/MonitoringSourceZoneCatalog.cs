using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

public static class MonitoringSourceZoneCatalog
{
    public static IReadOnlyList<string> SourceIds { get; } =
    [
        MonitoringSourceZoneIds.WindowsCpu,
        MonitoringSourceZoneIds.WindowsMemory,
        MonitoringSourceZoneIds.WindowsVirtualMemory,
        MonitoringSourceZoneIds.WindowsGpuAdapterOrder,
        MonitoringSourceZoneIds.PdhCpuFrequency,
        MonitoringSourceZoneIds.PdhGpuEngine,
        MonitoringSourceZoneIds.PdhSystemIo,
        MonitoringSourceZoneIds.VendorNvidiaNvml,
        MonitoringSourceZoneIds.VendorNvidiaNvapi,
        MonitoringSourceZoneIds.VendorAmdAdlx,
        MonitoringSourceZoneIds.VendorAmdSmu,
        MonitoringSourceZoneIds.WindowsStorageSensors,
        MonitoringSourceZoneIds.HardwareMonitorWmi,
        MonitoringSourceZoneIds.NotebookOemFan,
        MonitoringSourceZoneIds.IpHelperNetwork,
        MonitoringSourceZoneIds.EtwNetworkTcpIp
    ];

    public static IReadOnlyList<MonitoringSourceZone> CreateDefaultZones(string contentRootPath)
    {
        return
        [
            new WindowsCpuMonitoringZone(),
            new WindowsMemoryMonitoringZone(),
            new WindowsVirtualMemoryMonitoringZone(),
            new WindowsGpuAdapterOrderMonitoringZone(),
            new PdhCpuFrequencyMonitoringZone(),
            new PdhGpuEngineMonitoringZone(),
            new PdhSystemIoMonitoringZone(),
            new NvidiaNvmlMonitoringZone(),
            new NvidiaNvapiMonitoringZone(),
            new AmdAdlxMonitoringZone(),
            new AmdSmuMonitoringZone(contentRootPath),
            new WindowsStorageSensorsMonitoringZone(),
            new HardwareMonitorWmiMonitoringZone(),
            new NotebookOemFanMonitoringZone(),
            new IpHelperNetworkMonitoringZone(),
            new EtwNetworkTcpIpMonitoringZone()
        ];
    }
}
