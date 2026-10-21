namespace ResourceManager.ReadWrite.Windows.Cpu.Models;

/// <summary>One explicitly enumerated Fn8000_0026 APIC domain; the count is not
/// a promise that every addressable processor is active or visible to Windows.</summary>
public sealed record CpuAmdTopologyLevel(byte ApicShift, ushort LogicalProcessorCount);

/// <summary>Hardware topology from one affinity-verified logical processor.
/// This is independent of Windows die relationships and product CCD heuristics.</summary>
public sealed record CpuAmdExtendedTopology(uint ExtendedApicId,
    CpuAmdTopologyLevel Core, CpuAmdTopologyLevel Complex,
    CpuAmdTopologyLevel Ccd, CpuAmdTopologyLevel Socket);

/// <summary>Explicit links to objects in the same information publication.</summary>
public sealed record CpuAmdCcdCoreMembership(string CoreDeviceIdentity,
    string ComplexDeviceIdentity, IReadOnlyList<CpuProcessorAddress> LogicalProcessorAddresses);
