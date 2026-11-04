using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig.Adapters;

// These are resource names, NOT native source IDs or source preference ranks.
public enum MetricOwnerSdkSource
{
    Cpu, Memory, VirtualMemory, CpuFrequency, PdhGpu, Nvml, Nvapi, Adlx, Igcl,
    IntelMsr, AmdSmu, AmdCcd, Network, Battery, NpuEngine, NpuMemory,
    Storage, Nvme, Ata, DiskPerformance, HardwareMonitor, NotebookFan, SystemIo, GpuInventory
}

public sealed record MetricOwnerSdkSourceBinding(ulong NativeSourceId, MetricOwnerSdkSource Resource,
    bool ApplyUnavailableCooldown);

/// <summary>Cold raw topology references, keyed by Facts.Scopes ordinal, never an expanded catalog row.</summary>
public sealed class MetricOwnerSdkScope(uint nativeScopeIndex, string deviceKey, int displayIndex)
{
    public uint NativeScopeIndex { get; } = nativeScopeIndex;
    public string DeviceKey { get; } = deviceKey;
    public int DisplayIndex { get; } = displayIndex;
    public bool CanSample { get; init; } = true;
    public NumaMemoryCatalogIdentity? Numa { get; init; }
    public IntelMsrCatalogIdentity? IntelMsr { get; init; }
    public NetworkInterfaceCatalogIdentity? Network { get; init; }
    public BatteryCatalogIdentity? Battery { get; init; }
    public NpuEngineCatalogIdentity? NpuEngine { get; init; }
    public NpuMemoryCatalogIdentity? NpuMemory { get; init; }
    public AmdCcdCatalogIdentity? AmdCcd { get; init; }
    public NvidiaNvmlFanIdentity? NvmlFan { get; init; }
    public IntelGclFanIdentity? IgclFan { get; init; }
    public WindowsDiskPerformanceTarget? DiskPerformance { get; init; }
    public WindowsGpuAdapter? GpuAdapter { get; init; }
}

public sealed record MetricOwnerSdkTopology
{
    public WindowsGpuAdapterInventoryRead? GpuInventory { get; init; }
    public uint IgclFanMaximumInstanceCount { get; init; }
    public uint NpuEngineMaximumInstanceCount { get; init; }
    public uint NpuMemoryMaximumInstanceCount { get; init; }
    /// <summary>Exact existing SMU route evidence; null means no supplied GPU routing evidence.</summary>
    public string? SmuIntegratedGpuDeviceKey { get; init; }
}
