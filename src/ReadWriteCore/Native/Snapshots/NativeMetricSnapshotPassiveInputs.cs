using System.Globalization;
namespace ResourceManager.App.Infrastructure.Monitoring;

public readonly record struct NativeMetricSnapshotSourceObservationIdentity(
    ulong SourceHandle, ulong SourceIncarnation, ulong SourceObservationSequence);

public readonly record struct NativeMetricSnapshotCpuCounterObservation(
    bool IsAvailable, ulong IdleTicks, ulong KernelTicks, ulong UserTicks,
    ulong MonotonicTicks, ulong MonotonicTicksPerSecond, DateTimeOffset ObservedAt);

public readonly record struct NumaMemoryCatalogIdentity(ushort NodeNumber)
{
    public string ExactIdentityKey => $"windows-numa-node|{NodeNumber.ToString(CultureInfo.InvariantCulture)}";
}

public sealed record NetworkInterfaceCatalogIdentity(int DisplayIndex, Guid Guid, ulong Luid, uint Index)
{
    // Interface description declared by the same IP Helper row (MIB_IF_ROW2.Description); never synthesized.
    public string? NameDeclared { get; init; }
    public string ExactIdentityKey => "windows-network-interface|" + Guid.ToString("D").ToUpperInvariant();
    public string BindingKey => $"{ExactIdentityKey}|{Luid:X16}|{Index}";
}


public static class NativeMetricSnapshotSources
{
    public const string WindowsNpuEngine = "windows.npu-engine";
    public const string WindowsNpuMemory = "windows.npu-memory";
    public const string WindowsCpu = "windows.cpu";
    public const string WindowsMemory = "windows.memory";
    public const string WindowsVirtualMemory = "windows.virtual-memory";
    public const string WindowsGpuAdapterOrder = "windows.gpu-adapter-order";
    public const string PdhCpuFrequency = "pdh.cpu-frequency";
    public const string PdhGpuEngine = "pdh.gpu-engine";
    public const string NvidiaNvml = "nvidia.nvml";
    public const string NvidiaNvapi = "nvidia.nvapi";
    public const string AmdAdlx = "amd.adlx";
    public const string IntelGcl = "intel.igcl";
    public const string IntelMsr = "intel.msr";
    public const string AmdSmu = "amd.smu";
    public const string WindowsStorageSensors = "windows.storage-sensors";
    public const string WindowsNvmeHealth = "windows.nvme-health";
    public const string WindowsAtaSmart = "windows.ata-smart";
    public const string WindowsDiskPerformance = "windows.disk-performance";
    public const string WindowsBattery = "windows.battery";
    public const string HardwareMonitorWmi = "hardware-monitor.wmi";
    public const string NotebookOemFan = "notebook-oem.fan";
    public const string WindowsNetworkInterface = "windows.network-interface";
    public const string PdhSystemIo = "pdh.system-io";

}
