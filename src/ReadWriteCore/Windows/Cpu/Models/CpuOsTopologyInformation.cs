namespace ResourceManager.ReadWrite.Windows.Cpu.Models;

/// <summary>OS relationships and independently sourced information from this capture. Null groups are unavailable; empty groups are successful empty reads.</summary>
public sealed record CpuOsTopologyInformation(
    IReadOnlyList<CpuOsPhysicalCore>? PhysicalCores,
    IReadOnlyList<CpuCacheInformation>? Caches)
{
    public IReadOnlyList<CpuOsPackage>? Packages { get; init; }
    public IReadOnlyList<CpuOsDie>? Dies { get; init; }
    public IReadOnlyList<CpuOsNumaNode>? NumaNodes { get; init; }
    public IReadOnlyList<CpuThreadCpuidInformation>? ThreadCpuid { get; init; }
    public IReadOnlyList<string>? OsUsableFeatures { get; init; }
    public ulong? OsEnabledXStateMask { get; init; }
    /// <summary>Interval busy percentages indexed by group * 64 + index, from this capture's native times. Null entries have no valid interval.</summary>
    public IReadOnlyList<double?>? LogicalProcessorBusyPercent { get; init; }
}

public sealed record CpuOsNumaNode(uint NodeNumber, IReadOnlyList<int> LogicalProcessorIds);

public sealed record CpuOsPackage(string DeviceIdentity, IReadOnlyList<int> LogicalProcessorIds);

public sealed record CpuOsDie(string DeviceIdentity, IReadOnlyList<int> LogicalProcessorIds, string? PackageDeviceIdentity);

public sealed record CpuOsPhysicalCore(
    string PhysicalCoreId,
    byte EfficiencyClass,
    bool SmtRelationshipReported,
    IReadOnlyList<int> LogicalProcessorIds);

public sealed record CpuProcessorAddress(ushort ProcessorGroup, byte GroupRelativeIndex);
