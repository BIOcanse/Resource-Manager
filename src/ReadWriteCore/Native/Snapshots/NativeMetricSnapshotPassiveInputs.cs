using ResourceManager.ReadWrite;
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
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string AmdSmn = "amd.smn";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string WindowsNpuEngine = "windows.npu-engine";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string WindowsNpuMemory = "windows.npu-memory";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string WindowsCpu = "windows.cpu";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string WindowsMemory = "windows.memory";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string WindowsVirtualMemory = "windows.virtual-memory";
    [RuntimeRead(ReadVolatilityKind.EventDriven, "Windows GPU adapter topology changed")]
    public const string WindowsGpuAdapterOrder = "windows.gpu-adapter-order";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string PdhCpuFrequency = "pdh.cpu-frequency";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string PdhGpuEngine = "pdh.gpu-engine";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string NvidiaNvml = "nvidia.nvml";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string NvidiaNvapi = "nvidia.nvapi";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string AmdAdlx = "amd.adlx";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string IntelGcl = "intel.igcl";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string IntelMsr = "intel.msr";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string AmdSmu = "amd.smu";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string WindowsStorageSensors = "windows.storage-sensors";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string WindowsNvmeHealth = "windows.nvme-health";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string WindowsAtaSmart = "windows.ata-smart";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string WindowsDiskPerformance = "windows.disk-performance";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string WindowsBattery = "windows.battery";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string HardwareMonitorWmi = "hardware-monitor.core";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string NotebookOemFan = "notebook-oem.fan";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string WindowsNetworkInterface = "windows.network-interface";
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    public const string PdhSystemIo = "pdh.system-io";

}
