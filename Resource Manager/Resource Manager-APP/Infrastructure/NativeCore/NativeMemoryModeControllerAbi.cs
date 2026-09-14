using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeMemoryModeControllerAbi
{
    public const uint Version = 0x0006_0001;
}

internal enum NativeMemoryModeControllerStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    NoData = 3,
    BufferTooSmall = 4,
    StaleGeneration = 5,
    CapacityExceeded = 6,
    ConflictingFacts = 7,
    InvalidFacts = 8,
    OutOfMemory = 9,
    RecreateRequired = 10
}

internal enum NativeMemoryMode : byte
{
    Invalid = 0,
    Unrestricted = 1,
    Normal = 2,
    Optimize = 3,
    PagedFrozen = 4
}

[Flags]
internal enum NativeMemoryGradeSet : byte
{
    None = 0,
    Normal = 1 << 0,
    L1 = 1 << 1,
    L2 = 1 << 2,
    L3 = 1 << 3,
    L4 = 1 << 4,
    Known = Normal | L1 | L2 | L3 | L4
}

[Flags]
internal enum NativeMemoryModeEnvelopeValidity : ulong
{
    None = 0,
    MemorySource = 1UL << 0,
    MemoryPressure = 1UL << 1,
    CpuScores = 1UL << 2,
    Required = MemorySource | MemoryPressure | CpuScores,
    Known = Required
}

[Flags]
internal enum NativeMemoryModeEnvelopeFlags : ulong
{
    None = 0,
    FullReplacement = 1UL << 0,
    AllowUnrestricted = 1UL << 1,
    Required = FullReplacement,
    Known = Required | AllowUnrestricted
}

[Flags]
internal enum NativeMemoryModeSoftwareValidity : ulong
{
    None = 0,
    Identity = 1UL << 0,
    SchedulingGeneration = 1UL << 1,
    CpuScore = 1UL << 2,
    BaseScore = 1UL << 3,
    Required = Identity | SchedulingGeneration | CpuScore | BaseScore,
    Known = Required
}

[Flags]
internal enum NativeMemoryModeDesiredSoftwareFlags : byte
{
    None = 0,
    BaseScoreClamped = 1 << 0,
    Known = BaseScoreClamped
}

[Flags]
internal enum NativeMemoryModeDesiredSoftwareValidity : ulong
{
    None = 0,
    SoftwareIdentity = 1UL << 0,
    SnapshotGeneration = 1UL << 1,
    SchedulingGeneration = 1UL << 2,
    Score = 1UL << 3,
    Rank = 1UL << 4,
    Mode = 1UL << 5,
    BaseScore = 1UL << 6,
    BaseScoreAllowedGrades = 1UL << 7,
    Required = SoftwareIdentity | SnapshotGeneration | SchedulingGeneration |
        Score | Rank | Mode | BaseScore | BaseScoreAllowedGrades,
    Known = Required
}

[Flags]
internal enum NativeMemoryModeSnapshotFlags : ulong
{
    None = 0,
    FullReplacement = 1UL << 0,
    UnrestrictedAllowed = 1UL << 1,
    Known = FullReplacement | UnrestrictedAllowed
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 80)]
internal unsafe struct NativeMemoryModeConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public uint MaximumSoftwareCount;
    public uint MaximumOutputCount;
    public uint RatioUnitsMaximum;
    public uint UnrestrictedMinimumFreeRatioUnits;
    public uint NormalMinimumFreeRatioUnits;
    public uint StrongBeginFreeRatioUnits;
    public double MiddleTierMinimumBaseScore;
    public double HighTierMinimumBaseScore;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 64)]
internal unsafe struct NativeMemoryModeCapacity
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public uint SoftwareInputStructSize;
    public uint SoftwareOutputStructSize;
    public uint SoftwareCapacity;
    public uint OutputCapacity;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 112)]
internal unsafe struct NativeMemoryModeGenerationEnvelope
{
    public uint AbiVersion;
    public uint StructSize;
    public uint SoftwareInputStructSize;
    public uint SoftwareOutputStructSize;
    public ulong ConfigurationGeneration;
    public ulong SchedulingGeneration;
    public ulong MemorySourceWorkspaceIdentity;
    public ulong MemorySourceCommittedGeneration;
    public ulong SnapshotGeneration;
    public NativeMemoryModeEnvelopeValidity ValidMask;
    public NativeMemoryModeEnvelopeFlags Flags;
    public uint MemoryFreeRatioUnits;
    public uint Reserved0;
    public uint SoftwareCount;
    public uint OutputCapacity;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 64)]
internal unsafe struct NativeMemoryModeSoftwareInput
{
    public uint StructSize;
    public uint Flags;
    public NativeMemoryModeSoftwareValidity ValidMask;
    public ulong SoftwareKey;
    public ulong SchedulingGeneration;
    public double CpuScore;
    public double BaseScore;
    public uint SourceIndex;
    public uint Reserved0;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 64)]
internal struct NativeMemoryModeDesiredSoftwareOutput
{
    public uint StructSize;
    public NativeMemoryMode Mode;
    public NativeMemoryModeDesiredSoftwareFlags Flags;
    public NativeMemoryGradeSet BaseScoreAllowedGrades;
    public byte Reserved0;
    public NativeMemoryModeDesiredSoftwareValidity ValidMask;
    public ulong SoftwareKey;
    public ulong SnapshotGeneration;
    public ulong SchedulingGeneration;
    public double Score;
    public double BaseScore;
    public uint Rank;
    public uint SourceIndex;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 96)]
internal unsafe struct NativeMemoryModeSnapshot
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong SchedulingGeneration;
    public ulong MemorySourceWorkspaceIdentity;
    public ulong MemorySourceCommittedGeneration;
    public ulong SnapshotGeneration;
    public NativeMemoryModeSnapshotFlags Flags;
    public uint OutputCount;
    public uint OptimizeCount;
    public uint StrongestCount;
    public uint Reserved0;
    public fixed ulong Reserved[3];
}
