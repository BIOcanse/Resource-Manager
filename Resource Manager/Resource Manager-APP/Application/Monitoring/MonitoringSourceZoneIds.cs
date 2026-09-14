namespace ResourceManager.App.Application.Monitoring;

public static class MonitoringSourceZoneIds
{
    public const string WindowsCpu = "collector.windows.cpu";
    public const string WindowsMemory = "collector.windows.memory";
    public const string WindowsVirtualMemory = "collector.windows.virtual-memory";
    public const string WindowsGpuAdapterOrder = "collector.windows.gpu-adapter-order";
    public const string PdhCpuFrequency = "collector.pdh.cpu-frequency";
    public const string PdhGpuEngine = "collector.pdh.gpu-engine";
    public const string PdhSystemIo = "collector.pdh.system-io";
    public const string VendorNvidiaNvml = "collector.vendor.nvidia-nvml";
    public const string VendorNvidiaNvapi = "collector.vendor.nvidia-nvapi";
    public const string VendorAmdAdlx = "collector.vendor.amd-adlx";
    public const string VendorAmdSmu = "collector.vendor.amd-smu";
    public const string WindowsStorageSensors = "collector.windows.storage-sensors";
    public const string HardwareMonitorWmi = "collector.hardware-monitor.wmi";
    public const string NotebookOemFan = "collector.notebook-oem-fan";
    public const string IpHelperNetwork = "collector.ip-helper.network";
    public const string EtwNetworkTcpIp = "collector.etw.network-tcpip";
    public const string EtwCpuCoreResidency = "collector.etw.cpu-core-residency";
    public const string EtwFileIoDisk = "collector.etw.fileio-disk";
    public const string EtwSystemInterrupts = "collector.etw.system-interrupts";
    public const string EtwDxgkrnlVidMm = "collector.etw.dxgkrnl-vidmm";
}
