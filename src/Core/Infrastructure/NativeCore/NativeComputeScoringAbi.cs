using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeComputeScoringAbi
{
    public const uint Version = 0x0008_0000;
    public const int RuntimeStateCount = 7;
}

internal enum NativeComputeScoringStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    NoData = 3,
    BufferTooSmall = 4,
    CapacityExceeded = 6,
    ConflictingFacts = 7,
    InvalidFacts = 8,
    OutOfMemory = 9,
}

internal enum NativeComputeScoringRuntimeState : byte
{
    Unknown = 0,
    ForegroundFocused = 1,
    ForegroundUnfocused = 2,
    BackgroundWindow = 3,
    TrayOnly = 4,
    BackgroundProcess = 5,
    NotRunning = 6
}

internal enum NativeComputeScoringOutputKind : byte
{
    ProcessCpu = 1,
    ProcessGpu = 2,
    SoftwareCpu = 3,
    SoftwareGpu = 4,
    SoftwareMemory = 5
}

[Flags]
internal enum NativeComputeScoringConfigFields : ulong
{
    None = 0,
    Capacities = 1UL << 0,
    CpuMultipliers = 1UL << 1,
    GpuMultipliers = 1UL << 2,
    Bounds = 1UL << 3,
    Required = Capacities | CpuMultipliers | GpuMultipliers | Bounds,
    Known = Required
}

[Flags]
internal enum NativeComputeScoringEnvelopeValidity : ulong
{
    None = 0,
    WelfareCapacity = 1UL << 0,
    Known = WelfareCapacity
}

[Flags]
internal enum NativeComputeScoringEnvelopeFlags : ulong
{
    None = 0,
    CpuSnapshotComplete = 1UL << 0,
    GpuSnapshotComplete = 1UL << 1,
    Known = CpuSnapshotComplete | GpuSnapshotComplete
}

[Flags]
internal enum NativeComputeScoringProcessValidity : ulong
{
    None = 0,
    Identity = 1UL << 0,
    SoftwareIdentity = 1UL << 1,
    RuntimeState = 1UL << 2,
    BaseImportance = 1UL << 3,
    CpuPolicyMultiplier = 1UL << 4,
    CpuOccupancy = 1UL << 5,
    BaseRequired = Identity | SoftwareIdentity | RuntimeState | BaseImportance,
    CpuRequired = Identity | SoftwareIdentity | RuntimeState | BaseImportance |
        CpuPolicyMultiplier | CpuOccupancy,
    Known = CpuRequired
}

[Flags]
internal enum NativeComputeScoringProcessFlags : uint
{
    None = 0,
    Running = 1U << 0,
    CpuMetricsComplete = 1U << 1,
    GpuMetricsComplete = 1U << 2,
    Known = Running | CpuMetricsComplete | GpuMetricsComplete
}

[Flags]
internal enum NativeComputeScoringGpuValidity : ulong
{
    None = 0,
    ProcessIdentity = 1UL << 0,
    SoftwareIdentity = 1UL << 1,
    AdapterIdentity = 1UL << 2,
    GpuPolicyMultiplier = 1UL << 3,
    GpuOccupancy = 1UL << 4,
    Required = ProcessIdentity | SoftwareIdentity | AdapterIdentity |
        GpuPolicyMultiplier | GpuOccupancy,
    Known = Required
}

[Flags]
internal enum NativeComputeScoringSnapshotFlags : ulong
{
    None = 0,
    CpuScoresValid = 1UL << 0,
    GpuScoresValid = 1UL << 1,
    Known = CpuScoresValid | GpuScoresValid
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 200)]
internal unsafe struct NativeComputeScoringConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public NativeComputeScoringConfigFields FieldMask;
    public uint MaximumProcessCount;
    public uint MaximumGpuRowCount;
    public uint MaximumOutputCount;
    public uint CpuCoreCount;
    public fixed double CpuStateMultipliers[NativeComputeScoringAbi.RuntimeStateCount];
    public fixed double GpuStateMultipliers[NativeComputeScoringAbi.RuntimeStateCount];
    public double MaximumBaseImportance;
    public double MaximumPolicyMultiplier;
    public double CpuBaselineRatio;
    public double WelfareBaseScore;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 72)]
internal unsafe struct NativeComputeScoringCapacity
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public uint ProcessInputStructSize;
    public uint GpuInputStructSize;
    public uint OutputStructSize;
    public uint ProcessCapacity;
    public uint GpuCapacity;
    public uint OutputCapacity;
    public ulong ScratchByteCount;
    public uint ScratchAlignment;
    public uint Reserved0;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 104)]
internal unsafe struct NativeComputeScoringGenerationEnvelope
{
    public uint AbiVersion;
    public uint StructSize;
    public uint ProcessInputStructSize;
    public uint GpuInputStructSize;
    public uint OutputStructSize;
    public uint Reserved0;
    public ulong RoundNumber;
    public NativeComputeScoringEnvelopeValidity ValidMask;
    public NativeComputeScoringEnvelopeFlags Flags;
    public uint ProcessCount;
    public uint GpuRowCount;
    public uint GpuAdapterCount;
    public uint OutputCapacity;
    public double CpuFreeRatio;
    public double GpuFreeRatio;
    public double VramFreeRatio;
    public double MemoryFreeRatio;
    public uint WelfareEligibleProcessCount;
    public uint Reserved1;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 88)]
internal unsafe struct NativeComputeScoringProcessInput
{
    public uint StructSize;
    public NativeComputeScoringProcessFlags Flags;
    public NativeComputeScoringProcessValidity ValidMask;
    public ulong TargetKey;
    public ulong SoftwareKey;
    public ulong ProcessStartKey;
    public double BaseImportance;
    public double CpuPolicyMultiplier;
    public double WeightedCpuUsePercent;
    public uint SourceIndex;
    public uint ProcessId;
    public NativeComputeScoringRuntimeState RuntimeState;
    public fixed byte Reserved0[7];
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 88)]
internal unsafe struct NativeComputeScoringGpuInput
{
    public uint StructSize;
    public uint Flags;
    public NativeComputeScoringGpuValidity ValidMask;
    public ulong TargetKey;
    public ulong SoftwareKey;
    public ulong ProcessStartKey;
    public ulong AdapterKey;
    public double GpuPolicyMultiplier;
    public double GpuOccupancyPercent;
    public uint SourceIndex;
    public uint ProcessId;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 16)]
internal struct NativeComputeScoringCpuCoreInput
{
    public uint ProcessIndex;
    public uint CoreIndex;
    public double UsagePercent;
}

// Rows come in sections: process CPU, software CPU, process GPU, software GPU, software memory;
// each section is as long as its snapshot count. InputIndex names the caller's process input (CPU
// and memory sections) or GPU input (GPU sections); a software row names its first counted member.
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 16)]
internal struct NativeComputeScoringOutput
{
    public double Score;
    public uint InputIndex;
    public uint MemberCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 176)]
internal unsafe struct NativeComputeScoringSnapshot
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong RoundNumber;
    public NativeComputeScoringSnapshotFlags Flags;
    public uint ProcessInputCount;
    public uint GpuInputCount;
    public uint ProcessCpuOutputCount;
    public uint ProcessGpuOutputCount;
    public uint SoftwareCpuOutputCount;
    public uint SoftwareGpuOutputCount;
    public uint OutputCount;
    public uint InvalidFactCount;
    public double CpuFreeRatio;
    public double GpuFreeRatio;
    public double VramFreeRatio;
    public double MemoryFreeRatio;
    public double WelfareMultiplier;
    public double SystemPressure;
    public double WelfareBudget;
    public double WelfareShare;
    public uint WelfareEligibleProcessCount;
    public uint Reserved0;
    public ulong Reserved;
    public double CpuWelfareMultiplier;
    public double MemoryWelfareMultiplier;
    public double CpuWelfareBonus;
    public double MemoryWelfareBonus;
    public uint SoftwareMemoryOutputCount;
    public uint Reserved1;
}
