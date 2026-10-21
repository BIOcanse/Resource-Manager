using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed record NativeMetricSnapshotCatalogTopologySnapshot(
    ulong Generation,
    NativeMetricSnapshotCatalogTopologyStatus GpuStatus,
    IReadOnlyList<NativeMetricSnapshotGpuCatalogIdentity> GpuAdapters,
    NativeMetricSnapshotCatalogTopologyStatus StorageStatus,
    IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> StorageSensors,
    NativeMetricSnapshotCatalogTopologyStatus FanStatus,
    IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> SystemFans)
{
    public NativeMetricSnapshotCatalogTopologyStatus NpuEngineStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
    public IReadOnlyList<NpuEngineCatalogIdentity> NpuEngineIdentities { get; init; } = [];
    // Keep even a real count0 directory in the existing holder; identity inference loses it.
    public IWindowsNpuEngineDirectory? NpuEngineDirectory { get; init; }
    public IDisposable? NpuEngineDirectoryReference { get; init; }
    public NativeMetricSnapshotCatalogTopologyStatus NpuMemoryStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
    public IReadOnlyList<NpuMemoryCatalogIdentity> NpuMemoryIdentities { get; init; } = [];
    public NativeMetricSnapshotCatalogTopologyStatus IgclFanStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Complete;
    // The capturing operation owns this initial reference; a published holder takes its own counted lease.
    public IDisposable? IgclFanDirectoryReference { get; init; }
    public NativeMetricSnapshotCatalogTopologyStatus NvmlFanStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
    public NativeMetricSnapshotCatalogTopologyStatus NotebookOemFanStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Complete;
    public NativeMetricSnapshotCatalogTopologyStatus IntelCpuCoreStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
    public NativeMetricSnapshotCatalogTopologyStatus IntelCpuPackageStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
    public NativeMetricSnapshotCatalogTopologyStatus IntelCpuLogicalProcessorStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
    public NativeMetricSnapshotCatalogTopologyStatus IntelCpuDieStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
    public NativeMetricSnapshotCatalogTopologyStatus IntelPlatformStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
    public IReadOnlyList<IntelMsrCatalogIdentity> IntelCpuIdentities { get; init; } = [];
    public NativeMetricSnapshotCatalogTopologyStatus NetworkInterfaceStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
    public IReadOnlyList<NetworkInterfaceCatalogIdentity> NetworkInterfaces { get; init; } = [];
    public MetricVendor CpuVendor { get; init; } = MetricVendor.All;
    public NativeMetricSnapshotCatalogTopologyStatus BatteryStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
    public IReadOnlyList<BatteryCatalogIdentity> Batteries { get; init; } = [];
    public NativeMetricSnapshotCatalogTopologyStatus NumaStatus { get; init; } = NativeMetricSnapshotCatalogTopologyStatus.Unavailable;
    public IReadOnlyList<NumaMemoryCatalogIdentity> NumaNodes { get; init; } = [];
}

public sealed record NativeMetricSnapshotOrdinalTopologyRead(
    NativeMetricSnapshotCatalogTopologyStatus Status,
    IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> Identities);


