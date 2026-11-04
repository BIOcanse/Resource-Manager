using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Vendors.Nvidia;
namespace ResourceManager.ReadWrite.Windows.Metrics.Topology;
public sealed record WindowsMetricTopologyResources(Func<WindowsGpuAdapterInventoryRead> ReadGpuInventory,
    Func<ulong, NativeMetricSnapshotOrdinalTopologyRead> ReadStorageIdentities,
    Func<ulong, NativeMetricSnapshotOrdinalTopologyRead> ReadFanIdentities,
    Func<BatteryTopologyRead> ReadBatteryIdentities,
    Func<NetworkInterfaceTopologyRead> ReadNetworkIdentities,
    Func<NumaMemoryTopologyRead> ReadNumaIdentities)
{
    public Func<CpuOsTopologyInformation?, uint, AmdCcdTopologyRead> ReadAmdCcdIdentities { get; init; }
        = static (_, _) => new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
    public Func<uint, ResourceManager.ReadWrite.Windows.Devices.Npu.IWindowsNpuEngineDirectory?, NpuEngineTopologyRead> OpenNpuEngineDirectory { get; init; } = static (_, _) => new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
    public Func<uint, NpuMemoryTopologyRead> ReadNpuMemoryIdentities { get; init; } = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
    public Func<WindowsGpuAdapterInventoryRead, uint, IntelGclFanDirectoryLease?, IntelFanTopologyRead> OpenIgclFanDirectory { get; init; } = static (_, _, _) => new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
    public Func<WindowsGpuAdapterInventoryRead, IReadOnlyList<NvidiaNvmlFanIdentity>?> ReadNvmlFanIdentities { get; init; } = _ => null;
}
