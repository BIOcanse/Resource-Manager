using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Windows.Gpu.Control;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace ResourceManager.ReadWrite.Windows.Metrics.Writes;

/// <summary>Explicit borrowed original control resources and fresh identity inputs.
/// No lifetime, numeric cache, worker or restoration store is owned by this record.
/// CPU APIs do not select a package; the producer admits only a complete single-host domain.</summary>
public sealed record WindowsMetricControlResources
{
    public IntelCpuControlResource? IntelCpuControl { get; init; }
    public AmdCpuControlResource? AmdCpuControl { get; init; }
    public AmdDesktopGpuControlResource? AmdDesktopGpuControl { get; init; }
    public IntegratedGpuControlResource? IntegratedGpuControl { get; init; }
    public Func<CpuIdentityInformation?> ControlReadCpuIdentity { get; init; } = CpuIdentityInformationReader.Read;
    public Func<CpuOsTopologyInformation> ControlReadCpuTopology { get; init; }
        = static () => new WindowsCpuOsTopologyReader().ReadInformation(includeUsage: false);
    /// <summary>Original explicit chassis fact. Absence refuses AMD desktop admission;
    /// a caller must supply its authoritative chassis reader, never infer from battery/fan.</summary>
    public Func<bool> ControlIsFixedChassis { get; init; } = static () => false;
    /// <summary>Host may map the exact discovered frame to its original raw object ID
    /// so the borrowed resource captures/restores its existing baseline key. Null result refuses binding.</summary>
    public Func<HardwareMetricSnapshot, WindowsMetricControlFamily, MetricResource, string, ulong?, string?>?
        ControlResolveObjectId { get; init; }
}

/// <summary>Passive exact execution facts from explicit discovery, not another resolver.
/// Adapter ordinals are re-resolved using fresh LUID/PNP; CPU domain is not a serial number.</summary>
public sealed record WindowsMetricControlTarget(string? HostCpuIdentity = null,
    string? GpuDeviceIdentity = null, string? GpuPnp = null)
{
    public string? RawObjectId { get; init; }
}
