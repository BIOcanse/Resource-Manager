using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class NativeMetricSnapshotSourceCatalog
{
    internal const string WindowsCpu = NativeMetricSnapshotSources.WindowsCpu;
    internal const string WindowsMemory = NativeMetricSnapshotSources.WindowsMemory;
    internal const string WindowsVirtualMemory = NativeMetricSnapshotSources.WindowsVirtualMemory;
    internal const string WindowsGpuAdapterOrder = NativeMetricSnapshotSources.WindowsGpuAdapterOrder;
    internal const string PdhCpuFrequency = NativeMetricSnapshotSources.PdhCpuFrequency;
    internal const string PdhGpuEngine = NativeMetricSnapshotSources.PdhGpuEngine;
    internal const string NvidiaNvml = NativeMetricSnapshotSources.NvidiaNvml;
    internal const string NvidiaNvapi = NativeMetricSnapshotSources.NvidiaNvapi;
    internal const string AmdAdlx = NativeMetricSnapshotSources.AmdAdlx;
    internal const string IntelGcl = NativeMetricSnapshotSources.IntelGcl;
    internal const string IntelMsr = NativeMetricSnapshotSources.IntelMsr;
    internal const string AmdSmu = NativeMetricSnapshotSources.AmdSmu;
    internal const string AmdSmn = NativeMetricSnapshotSources.AmdSmn;
    internal const string WindowsStorageSensors = NativeMetricSnapshotSources.WindowsStorageSensors;
    internal const string WindowsNvmeHealth = NativeMetricSnapshotSources.WindowsNvmeHealth;
    internal const string WindowsAtaSmart = NativeMetricSnapshotSources.WindowsAtaSmart;
    internal const string WindowsDiskPerformance = NativeMetricSnapshotSources.WindowsDiskPerformance;
    internal const string WindowsNpuEngine = NativeMetricSnapshotSources.WindowsNpuEngine;
    internal const string WindowsNpuMemory = NativeMetricSnapshotSources.WindowsNpuMemory;
    internal const string WindowsBattery = NativeMetricSnapshotSources.WindowsBattery;
    internal const string HardwareMonitorWmi = NativeMetricSnapshotSources.HardwareMonitorWmi;
    internal const string NotebookOemFan = NativeMetricSnapshotSources.NotebookOemFan;
    internal const string WindowsNetworkInterface = NativeMetricSnapshotSources.WindowsNetworkInterface;
    internal const string PdhSystemIo = NativeMetricSnapshotSources.PdhSystemIo;

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
            IntelGcl => MonitoringSourceZoneIds.VendorIntelGcl,
            IntelMsr => MonitoringSourceZoneIds.VendorIntelMsr,
            AmdSmu or AmdSmn => MonitoringSourceZoneIds.VendorAmdSmu,
            WindowsStorageSensors or WindowsNvmeHealth or WindowsAtaSmart or WindowsDiskPerformance =>
                MonitoringSourceZoneIds.WindowsStorageSensors,
            WindowsNpuEngine => MonitoringSourceZoneIds.WindowsNpuEngine,
            WindowsNpuMemory => MonitoringSourceZoneIds.WindowsNpuMemory,
            WindowsBattery => MonitoringSourceZoneIds.WindowsBattery,
            HardwareMonitorWmi =>
                MonitoringSourceZoneIds.HardwareMonitorWmi,
            NotebookOemFan => MonitoringSourceZoneIds.NotebookOemFan,
            WindowsNetworkInterface => MonitoringSourceZoneIds.IpHelperNetwork,
            PdhSystemIo => MonitoringSourceZoneIds.PdhSystemIo,
            _ => throw new InvalidOperationException(
                $"Unknown native metric source '{sourceId}'.")
        };
}
