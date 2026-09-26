using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeFileQueryAbi
{
    public const uint Version = 0x0002_0000;
    public const uint UnicodeTokenizerVersion = 0x0006_0100;
    public const uint UnicodeRemoveDiacriticsMode = 2;
    public const uint TrigramTokenizerContractVersion = 0x0001_0000;
    public const uint TextMatchingVersion = 0x0001_0000;
}

internal enum NativeFileQueryStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    Unavailable = 3,
    NoData = 4,
    BufferTooSmall = 5,
    StaleFrame = 6,
    OutOfMemory = 7
}

internal enum NativeFileQueryPhase : uint
{
    Empty = 1,
    Planned = 2,
    Collecting = 3,
    Finalized = 4
}

internal enum NativeFileQueryPlanMode : uint
{
    ShortScan = 1,
    Fts = 2
}

internal enum NativeFileQuerySource : uint
{
    FileName = 1,
    RelativePath = 2,
    SoftwareName = 3,
    ShortScan = 4
}

internal enum NativeFileQueryPrimitive : uint
{
    ShortSubstringOrderedScan = 1,
    FileNameTrigramFts = 2,
    RelativePathUnicodeFts = 3,
    SoftwareNameTrigramFts = 4
}

[Flags]
internal enum NativeFileQuerySourceMask : uint
{
    None = 0,
    FileName = 1U << 0,
    RelativePath = 1U << 1,
    SoftwareName = 1U << 2,
    ShortScan = 1U << 3,
    Known = FileName | RelativePath | SoftwareName | ShortScan
}

[Flags]
internal enum NativeFileQueryBeginValidity : ulong
{
    None = 0,
    OperationEpoch = 1UL << 0,
    QueryEpoch = 1UL << 1,
    QueryBytes = 1UL << 2,
    ResultLimit = 1UL << 3,
    Required = OperationEpoch | QueryEpoch | QueryBytes | ResultLimit,
    Known = Required
}

[Flags]
internal enum NativeFileQuerySubmitValidity : ulong
{
    None = 0,
    OperationEpoch = 1UL << 0,
    QueryEpoch = 1UL << 1,
    BatchEpoch = 1UL << 2,
    Candidates = 1UL << 3,
    CandidateBytes = 1UL << 4,
    Required = OperationEpoch | QueryEpoch | BatchEpoch | Candidates | CandidateBytes,
    Known = Required
}

[Flags]
internal enum NativeFileQueryFinalizeValidity : ulong
{
    None = 0,
    OperationEpoch = 1UL << 0,
    QueryEpoch = 1UL << 1,
    ResultCapacity = 1UL << 2,
    Required = OperationEpoch | QueryEpoch | ResultCapacity,
    Known = Required
}

[Flags]
internal enum NativeFileQueryResetValidity : ulong
{
    None = 0,
    OperationEpoch = 1UL << 0,
    Required = OperationEpoch,
    Known = Required
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQueryConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public uint MaximumQueryUtf8ByteCount;
    public uint MaximumQueryRuneCount;
    public uint MaximumPlanUtf8ByteCount;
    public uint MaximumSourcePlanCount;
    public uint MaximumCandidateCountPerSource;
    public uint MaximumSubmittedCandidateCount;
    public uint MaximumUniqueCandidateCount;
    public uint MaximumCandidateSubmitBatchCount;
    public uint MaximumCandidateSubmitUtf8ByteCount;
    public uint CandidateTextArenaByteCount;
    public uint MaximumFileNameUtf8ByteCount;
    public uint MaximumResultCount;
    public uint EntryIndexCapacity;
    public uint OrdinalIndexCapacity;
    public uint ShortQueryRuneThreshold;
    public uint UnicodeTokenizerVersion;
    public uint UnicodeRemoveDiacriticsMode;
    public uint TrigramTokenizerContractVersion;
    public uint CandidateLimitMultiplier;
    public uint CandidateLimitFloor;
    public uint CandidateLimitCeiling;
    public uint FileNamePriority;
    public uint RelativePathPriority;
    public uint SoftwareNamePriority;
    public uint Flags;
    public uint TextMatchingVersion;
    public ulong ResidentByteBudget;
    public fixed ulong Reserved[5];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQueryCapacity
{
    public uint StructSize;
    public uint QueryUtf8ByteCapacity;
    public uint QueryRuneCapacity;
    public uint PlanUtf8ByteCapacity;
    public uint SourcePlanCapacity;
    public uint CandidateCapacityPerSource;
    public uint SubmittedCandidateCapacity;
    public uint UniqueCandidateCapacity;
    public uint CandidateSubmitBatchCapacity;
    public uint CandidateSubmitUtf8ByteCapacity;
    public uint CandidateTextArenaByteCapacity;
    public uint FileNameUtf8ByteCapacity;
    public uint ResultCapacity;
    public uint EntryIndexCapacity;
    public uint OrdinalIndexCapacity;
    public uint ReservedU32;
    public ulong ResidentByteCount;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQueryBeginInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong QueryEpoch;
    public uint QueryByteCount;
    public uint RequestedResultCount;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQuerySourcePlan
{
    public uint StructSize;
    public uint SourceId;
    public uint PrimitiveKind;
    public uint Priority;
    public uint ExpressionOffset;
    public uint ExpressionLength;
    public uint CandidateLimit;
    public uint Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQueryPlanOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong StateRevision;
    public ulong QueryEpoch;
    public uint Mode;
    public uint SourcePlanCount;
    public uint QueryRuneCount;
    public uint RequestedResultCount;
    public uint PlanUtf8ByteCount;
    public uint SourceMask;
    public uint CandidateLimitPerSource;
    public uint MaximumTotalCandidateCount;
    public uint NormalizedQueryOffset;
    public uint NormalizedQueryLength;
    public uint UnicodeTokenizerVersion;
    public uint UnicodeRemoveDiacriticsMode;
    public uint TrigramTokenizerContractVersion;
    public uint TextMatchingVersion;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQueryCandidateInput
{
    public uint StructSize;
    public uint SourceId;
    public ulong EntryHandle;
    public uint CandidateOrdinal;
    public uint FileNameOffset;
    public uint FileNameLength;
    public uint RelativePathOffset;
    public uint RelativePathLength;
    public uint SoftwareNameOffset;
    public uint SoftwareNameLength;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQuerySubmitInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong QueryEpoch;
    public ulong BatchEpoch;
    public uint CandidateCount;
    public uint CandidateByteCount;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQuerySubmitOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong StateRevision;
    public ulong QueryEpoch;
    public ulong BatchEpoch;
    public uint AcceptedCandidateCount;
    public uint DuplicateCandidateCount;
    public uint TotalSubmittedCandidateCount;
    public uint TotalUniqueCandidateCount;
    public uint CandidateTextByteCount;
    public uint Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQueryFinalizeInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong QueryEpoch;
    public uint ResultCapacity;
    public uint ReservedU32;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQueryResult
{
    public uint StructSize;
    public uint SourcePriority;
    public ulong EntryHandle;
    public uint CandidateOrdinal;
    public uint FileNameRuneCount;
    public uint OrderIndex;
    public uint Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQueryFinalizeOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong StateRevision;
    public ulong QueryEpoch;
    public uint ResultCount;
    public uint MatchedCandidateCount;
    public uint SubmittedCandidateCount;
    public uint UniqueCandidateCount;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQueryResetInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFileQuerySnapshot
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong StateRevision;
    public ulong LastOperationEpoch;
    public ulong LastQueryEpoch;
    public ulong LastBatchEpoch;
    public uint Phase;
    public uint SourcePlanCount;
    public uint SubmittedCandidateCount;
    public uint UniqueCandidateCount;
    public uint MatchedCandidateCount;
    public uint ResultCount;
    public uint QueryByteCount;
    public uint PlanByteCount;
    public uint CandidateTextByteCount;
    public ulong ResidentByteCount;
    public ulong Flags;
    public fixed ulong Reserved[3];
}
