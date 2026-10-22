using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Pdh;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;

namespace ResourceManager.ReadWrite.Windows.Metrics.Resources;

/// <summary>Typed original source resources. Host callbacks borrow existing zone resources; CreateDefault owns actual SDK readers.
/// No source selection, loop or additional value cache lives in this bundle.</summary>
public sealed partial class WindowsHardwareMetricResources : IDisposable
{
    /// <summary>Borrowed unless registered by CreateDefault; the same resource owns detection and clock-pair state.</summary>
    public NvidiaGpuControlResource? NvidiaControl { get; init; }
    /// <summary>Borrowed original control resources. CreateDefault registers actual owned resources once.</summary>
    public WindowsMetricControlResources? Controls { get; init; }
    private readonly List<IDisposable> owned = [];
    internal void Own(IDisposable resource) => owned.Add(resource);
    public required string CpuName { get; init; }
    public required string MemoryDescription { get; init; }
    public required Func<NativeMetricSnapshotCpuCounterObservation> ReadCpuCounters { get; init; }
    public required Func<MemoryMetrics> ReadMemory { get; init; }
    public required Func<VirtualMemoryMetrics> ReadVirtualMemory { get; init; }
    public required Func<IReadOnlyList<NumaMemoryCatalogIdentity>, IReadOnlySet<string>, IReadOnlyDictionary<string, ulong>> ReadNumaAvailable { get; init; }
    public Func<IReadOnlyList<NumaMemoryCatalogIdentity>, IReadOnlySet<string>, IReadOnlyDictionary<string, ulong>> ReadNumaCapacityMiB { get; init; }
        = static (_, _) => new Dictionary<string, ulong>(StringComparer.Ordinal);
    public required Func<NumaMemoryTopologyRead> ReadNumaIdentities { get; init; }
    public required Func<WindowsGpuAdapterInventoryRead> ReadGpuInventory { get; init; }
    public required Func<CpuFrequency> ReadCpuFrequency { get; init; }
    public required Func<IReadOnlyList<WindowsGpuAdapter>, PdhGpuEngineUsageRead> ReadGpuUsage { get; init; }
    public required Func<NvidiaNvmlReadRequest, WindowsGpuAdapterInventoryRead, CancellationToken, Task<IReadOnlyList<GpuMetrics>>> ReadNvmlAsync { get; init; }
    public Func<WindowsGpuAdapterInventoryRead, IReadOnlyList<NvidiaNvmlFanIdentity>?> ReadNvmlFanIdentities { get; init; } = _ => null;
    public Func<IReadOnlyList<NvidiaNvmlFanReadTarget>, WindowsGpuAdapterInventoryRead, CancellationToken, Task<IReadOnlyList<NvidiaNvmlFanReading>>> ReadNvmlFansAsync { get; init; }
        = (_, _, _) => Task.FromResult<IReadOnlyList<NvidiaNvmlFanReading>>([]);
    /// <summary>Optional selected DRS database read. Injected sources are borrowed, not owned.</summary>
    public NvidiaDrsReadSource ReadNvapiDrs { get; init; } = static (_, _, _) => null;
    /// <summary>Explicit full-session discovery; ordinary topology does not call this delegate.</summary>
    public NvidiaDrsDirectorySource DiscoverNvapiDrs { get; init; } = static (_, _) => null;
    public required Func<NvidiaNvapiReadRequest, WindowsGpuAdapterInventoryRead, CancellationToken, Task<IReadOnlyList<NvidiaNvapiGpuSensor>>> ReadNvapiAsync { get; init; }
    public required Func<AmdAdlxReadRequest, WindowsGpuAdapterInventoryRead, CancellationToken, Task<IReadOnlyList<GpuMetrics>>> ReadAdlxAsync { get; init; }
    public required Func<AmdSmuCpuSensorReadRequest, CpuSensorMetrics> ReadAmdSmu { get; init; }
    public Func<WindowsGpuAdapterInventoryRead, uint, IntelGclFanDirectoryLease?, IntelFanTopologyRead> OpenIgclFanDirectory { get; init; } = static (_, _, _) => new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
    public Func<IReadOnlyList<IntelGclFanIdentity>, WindowsGpuAdapterInventoryRead, uint, CancellationToken, IReadOnlyList<IntelGclFanReading>> ReadIgclFans { get; init; } = static (_, _, _, _) => [];
    public required Func<WindowsGpuAdapterInventoryRead, IReadOnlyDictionary<int, IReadOnlySet<MetricFieldCode>>, CancellationToken, IReadOnlyList<IntelGclMetricRead>> ReadIntelGpu { get; init; }
    public required Func<IReadOnlyList<IntelMsrCatalogIdentity>, IReadOnlyDictionary<string, IReadOnlySet<MetricFieldCode>>, IReadOnlyList<IntelMsrMetricRead>> ReadIntelMsr { get; init; }
    public Func<uint, IWindowsNpuEngineDirectory?, NpuEngineTopologyRead> OpenNpuEngineDirectory { get; init; }
        = static (_, _) => new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
    public Func<IReadOnlyList<NpuEngineCatalogIdentity>, IReadOnlySet<string>, uint, CancellationToken, IReadOnlyList<NpuEngineRead>> ReadNpuEngines { get; init; }
        = static (_, _, _, _) => [];
    public Func<uint, NpuMemoryTopologyRead> ReadNpuMemoryIdentities { get; init; } = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
    public Func<IReadOnlyList<NpuMemoryCatalogIdentity>, IReadOnlyDictionary<string, NpuMemoryFields>, uint, CancellationToken, IReadOnlyList<NpuMemoryRead>> ReadNpuMemory { get; init; }
        = static (_, _, _, _) => [];
    public required Func<BatteryTopologyRead> ReadBatteryIdentities { get; init; }
    public required Func<IReadOnlyList<BatteryCatalogIdentity>, IReadOnlyDictionary<int, IReadOnlySet<MetricFieldCode>>, IReadOnlyList<BatteryMetricRead>> ReadBattery { get; init; }
    public required Func<NetworkInterfaceTopologyRead> ReadNetworkIdentities { get; init; }
    public required Func<IReadOnlyList<NetworkInterfaceCatalogIdentity>, IReadOnlyDictionary<string, IReadOnlySet<MetricFieldCode>>, IReadOnlyList<NetworkInterfaceMetricRead>> ReadNetworkInterfaces { get; init; }
    public required Func<PdhSystemIoReadRequest, PdhSystemIoMetrics> ReadSystemIo { get; init; }
    public required Func<ulong, NativeMetricSnapshotOrdinalTopologyRead> ReadStorageIdentities { get; init; }
    /// <summary>Optional selected-disk performance producer. CreateDefault supplies the native producer; injected resources remain caller-owned.</summary>
    public Func<IReadOnlyList<WindowsDiskPerformanceTarget>, CancellationToken, Task<WindowsDiskPerformanceCaptureResult>> ReadDiskPerformanceAsync { get; init; }
        = (_, _) => Task.FromResult(WindowsDiskPerformanceCaptureResult.Empty([]));
    public required Func<ulong, NativeMetricSnapshotOrdinalTopologyRead> ReadFanIdentities { get; init; }
    public required Func<StorageSensorSnapshot> ReadStorageSensors { get; init; }
    public required Func<StorageSensorReadRequest, StorageSensorSnapshot> ReadNvmeSensors { get; init; }
    public required Func<AtaSmartSensorReadRequest, StorageSensorSnapshot> ReadAtaSensors { get; init; }
    public required Func<HardwareMonitorSensorSnapshot> ReadHardwareMonitor { get; init; }
    public required Func<NotebookOemFanSensorSnapshot> ReadNotebookFans { get; init; }
    public required Action<bool> SynchronizeNotebookDemand { get; init; }
    public void Dispose()
    {
        List<Exception>? failures = null;
        for (var i = owned.Count - 1; i >= 0; i--)
        {
            try { owned[i].Dispose(); owned.RemoveAt(i); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException("Owned source cleanup remains incomplete.", failures);
    }
}

