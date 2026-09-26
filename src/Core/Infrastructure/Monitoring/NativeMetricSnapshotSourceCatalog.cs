using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class NativeMetricSnapshotSourceCatalog
{
    internal const string WindowsCpu = "windows.cpu";
    internal const string WindowsMemory = "windows.memory";
    internal const string WindowsVirtualMemory = "windows.virtual-memory";
    internal const string WindowsGpuAdapterOrder = "windows.gpu-adapter-order";
    internal const string PdhCpuFrequency = "pdh.cpu-frequency";
    internal const string PdhGpuEngine = "pdh.gpu-engine";
    internal const string NvidiaNvml = "nvidia.nvml";
    internal const string NvidiaNvapi = "nvidia.nvapi";
    internal const string AmdAdlx = "amd.adlx";
    internal const string AmdSmu = "amd.smu";
    internal const string WindowsStorageSensors = "windows.storage-sensors";
    internal const string HardwareMonitorWmi = "hardware-monitor.wmi";
    internal const string NotebookOemFan = "notebook-oem.fan";
    internal const string PdhSystemIo = "pdh.system-io";

    internal static string ResolveZoneId(string sourceId)
        => sourceId switch
        {
            WindowsCpu => MonitoringSourceZoneIds.WindowsCpu,
            WindowsMemory => MonitoringSourceZoneIds.WindowsMemory,
            WindowsVirtualMemory =>
                MonitoringSourceZoneIds.WindowsVirtualMemory,
            WindowsGpuAdapterOrder =>
                MonitoringSourceZoneIds.WindowsGpuAdapterOrder,
            PdhCpuFrequency => MonitoringSourceZoneIds.PdhCpuFrequency,
            PdhGpuEngine => MonitoringSourceZoneIds.PdhGpuEngine,
            NvidiaNvml => MonitoringSourceZoneIds.VendorNvidiaNvml,
            NvidiaNvapi => MonitoringSourceZoneIds.VendorNvidiaNvapi,
            AmdAdlx => MonitoringSourceZoneIds.VendorAmdAdlx,
            AmdSmu => MonitoringSourceZoneIds.VendorAmdSmu,
            WindowsStorageSensors =>
                MonitoringSourceZoneIds.WindowsStorageSensors,
            HardwareMonitorWmi =>
                MonitoringSourceZoneIds.HardwareMonitorWmi,
            NotebookOemFan => MonitoringSourceZoneIds.NotebookOemFan,
            PdhSystemIo => MonitoringSourceZoneIds.PdhSystemIo,
            _ => throw new InvalidOperationException(
                $"Unknown native metric source '{sourceId}'.")
        };
}
