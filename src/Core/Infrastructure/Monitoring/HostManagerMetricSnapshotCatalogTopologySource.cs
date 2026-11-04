using ResourceManager.App.Application.CpuTopology;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed class HostManagerMetricSnapshotCatalogTopologySource
{
    private readonly WindowsHardwareMetricTopologySource? source;
    private readonly Func<uint, uint, NativeMetricSnapshotCatalogProjection?, NativeMetricSnapshotCatalogTopologySnapshot>? captureWithPrior;
    private readonly Func<NativeMetricSnapshotCatalogTopologySnapshot>? capture;
    private readonly Func<uint, uint, NativeMetricSnapshotCatalogProjection?, uint, NativeMetricSnapshotCatalogTopologySnapshot>? captureWithEngine;
    public HostManagerMetricSnapshotCatalogTopologySource(WindowsGpuAdapterOrderMonitoringZone gpuAdapterOrderZone,
        WindowsBatteryMonitoringZone batteryZone, ICpuTopologySampler? cpuTopologySampler = null, IpHelperNetworkMonitoringZone? networkZone = null,
        NvidiaNvmlMonitoringZone? nvidiaZone = null, WindowsNpuMemoryMonitoringZone? npuMemoryZone = null, IntelGclMonitoringZone? intelGclZone = null, WindowsNpuEngineMonitoringZone? npuEngineZone = null,
        HardwareMonitorWmiMonitoringZone? hardwareMonitorZone = null, AmdSmuMonitoringZone? amdSmuZone = null)
    {
        var engines = npuEngineZone ?? new WindowsNpuEngineMonitoringZone();
        var npus = npuMemoryZone ?? new WindowsNpuMemoryMonitoringZone();
        var network = networkZone ?? new IpHelperNetworkMonitoringZone();
        source = new(new(() => gpuAdapterOrderZone.ReadInventory(requested: true),
            generation => WindowsStorageSensorsMonitoringZone.ReadIdentities(generation),
            generation => hardwareMonitorZone?.ReadFanCatalog(generation)
                ?? new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []),
            batteryZone.ReadIdentities, network.ReadInterfaceIdentities, WindowsMemoryMonitoringZone.ReadNumaIdentities)
            { OpenIgclFanDirectory = (inventory, maximum, prior) => intelGclZone?.OpenFanTopology(inventory, maximum, prior)
                    ?? new IntelFanTopologyRead(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []),
                OpenNpuEngineDirectory = engines.OpenDirectory,
                ReadAmdCcdIdentities = (cpu, maximum) => amdSmuZone?.ReadCcdIdentities(cpu, maximum)
                    ?? new(ResourceManager.ReadWrite.Native.NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []),
                ReadNvmlFanIdentities = inventory => nvidiaZone?.ReadFanIdentities(inventory), ReadNpuMemoryIdentities = npus.ReadIdentities },
            () => cpuTopologySampler?.CaptureTopology().OsTopologyInformation);
    }
    internal HostManagerMetricSnapshotCatalogTopologySource(Func<NativeMetricSnapshotCatalogTopologySnapshot> capture)
        => this.capture = capture;
    internal HostManagerMetricSnapshotCatalogTopologySource(Func<uint, uint, NativeMetricSnapshotCatalogProjection?, NativeMetricSnapshotCatalogTopologySnapshot> capture)
        => captureWithPrior = capture;
    internal HostManagerMetricSnapshotCatalogTopologySource(Func<uint, uint, NativeMetricSnapshotCatalogProjection?, uint, NativeMetricSnapshotCatalogTopologySnapshot> capture)
        => captureWithEngine = capture;
    internal NativeMetricSnapshotCatalogTopologySnapshot Capture() => Capture(0);
    internal NativeMetricSnapshotCatalogTopologySnapshot Capture(uint effectiveNpuMemoryMaximumInstanceCount) => Capture(effectiveNpuMemoryMaximumInstanceCount, 0);
    internal NativeMetricSnapshotCatalogTopologySnapshot Capture(uint effectiveNpuMemoryMaximumInstanceCount, uint effectiveIntelFanMaximumInstanceCount,
        NativeMetricSnapshotCatalogProjection? prior = null, uint effectiveNpuEngineMaximumInstanceCount = 0, uint effectiveAmdCcdMaximumInstanceCount = 0)
        => captureWithEngine?.Invoke(effectiveNpuMemoryMaximumInstanceCount, effectiveIntelFanMaximumInstanceCount, prior, effectiveNpuEngineMaximumInstanceCount)
            ?? captureWithPrior?.Invoke(effectiveNpuMemoryMaximumInstanceCount, effectiveIntelFanMaximumInstanceCount, prior)
            ?? capture?.Invoke() ?? source!.Capture(effectiveNpuMemoryMaximumInstanceCount, effectiveIntelFanMaximumInstanceCount, prior, effectiveNpuEngineMaximumInstanceCount, effectiveAmdCcdMaximumInstanceCount);
}
