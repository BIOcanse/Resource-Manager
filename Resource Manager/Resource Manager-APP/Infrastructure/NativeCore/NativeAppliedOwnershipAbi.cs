using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeAppliedOwnershipAbi
{
    public const uint Version = 0x0002_0000;
    public const ulong ImageMagic = 0x524d_4150_4f57_4e32;
    public const ulong ImageHeaderSize = 128;

    public const uint CreateConfigurationSize = 96;
    public const uint OpenConfigurationSize = 72;
    public const uint CapacitySize = 64;
    public const uint PrimaryIdentitySize = 40;
    public const uint ActionIdentitySize = 64;
    public const uint OriginalBindingSize = 160;
    public const uint DurablePayloadReferenceSize = 32;
    public const uint CurrentGradesSize = 24;
    public const uint OwnershipCasSize = 192;
    public const uint RecordSize = 320;
    public const uint PromoteInputSize = 320;
    public const uint TransitionInputSize = 448;
    public const uint RemoveInputSize = 224;
    public const uint SnapshotHeaderSize = 96;
}

internal enum NativeAppliedOwnershipStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    CapacityFull = 3,
    NoData = 4,
    StaleRevision = 5,
    DuplicateIdentity = 6,
    DuplicatePayload = 7,
    IdentityMismatch = 8,
    PayloadMismatch = 9,
    GradeMismatch = 10,
    CorruptImage = 11,
    TruncatedImage = 12,
    ChecksumMismatch = 13,
    NonCanonicalImage = 14,
    ConfigurationMismatch = 15,
    BufferTooSmall = 16,
    RevisionExhausted = 17,
    OutOfMemory = 18,
    InvalidTime = 19
}

internal enum NativeAppliedOwnershipScope : uint
{
    Process = 1,
    Adapter = 2
}

internal enum NativeAppliedOwnershipJournalScope : uint
{
    Process = 1,
    Software = 2
}

internal enum NativeAppliedOwnershipDisposition : uint
{
    Apply = 1,
    Restore = 2
}

[Flags]
internal enum NativeAppliedOwnershipDomain : uint
{
    None = 0,
    Process = 1U << 0,
    Cpu = 1U << 1,
    Gpu = 1U << 2,
    PhysicalMemory = 1U << 3,
    Known = Process | Cpu | Gpu | PhysicalMemory
}

[Flags]
internal enum NativeAppliedOwnershipGradeValidity : uint
{
    None = 0,
    Process = 1U << 0,
    Cpu = 1U << 1,
    Gpu = 1U << 2,
    Memory = 1U << 3,
    Known = Process | Cpu | Gpu | Memory
}

internal enum NativeAppliedOwnershipProcessGrade : int
{
    Level4 = -4,
    Level3 = -3,
    Level2 = -2,
    Level1 = -1,
    Normal = 0,
    A1 = 1
}

internal enum NativeAppliedOwnershipAdapterGrade : int
{
    Freeze = 0,
    Optimize = 1,
    Normal = 2,
    Extreme = 3
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAppliedOwnershipCreateConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong LedgerInstanceLow;
    public ulong LedgerInstanceHigh;
    public uint RecordCapacity;
    public uint PrimaryIndexCapacity;
    public uint PayloadIndexCapacity;
    public uint Flags;
    public ulong MaximumResidentBytes;
    public ulong MaximumImageBytes;
    public fixed ulong Reserved[5];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAppliedOwnershipOpenConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public uint MaximumRecordCapacity;
    public uint MaximumPrimaryIndexCapacity;
    public uint MaximumPayloadIndexCapacity;
    public uint Flags;
    public ulong MaximumResidentBytes;
    public ulong MaximumImageBytes;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAppliedOwnershipCapacity
{
    public uint StructSize;
    public uint RecordCapacity;
    public uint PrimaryIndexCapacity;
    public uint PayloadIndexCapacity;
    public uint RecordSize;
    public uint Flags;
    public ulong MaximumImageBytes;
    public ulong ResidentBytes;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAppliedOwnershipPrimaryIdentity
{
    public uint Scope;
    public uint ReservedUInt32;
    public ulong TargetId;
    public ulong SoftwareId;
    public ulong ProcessStartKey;
    public uint ProcessId;
    public uint ProcessReserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAppliedOwnershipActionIdentity
{
    public ulong ConfigurationGeneration;
    public ulong PlanEpoch;
    public ulong ActionId;
    public ulong HostSessionIncarnation;
    public ulong TargetId;
    public ulong SoftwareId;
    public ulong ProcessStartKey;
    public uint ProcessId;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAppliedOwnershipOriginalBinding
{
    public ulong JournalInstanceLow;
    public ulong JournalInstanceHigh;
    public NativeAppliedOwnershipActionIdentity ActionIdentity;
    public uint Scope;
    public uint Disposition;
    public uint DomainMask;
    public uint GradeValidMask;
    public int ProcessFromGrade;
    public int ProcessToGrade;
    public int CpuFromGrade;
    public int CpuToGrade;
    public int GpuFromGrade;
    public int GpuToGrade;
    public uint StableSystemStatus;
    public uint StableSystemError;
    public uint MaximumRecoveryAttempts;
    public uint RecoveryReserved;
    public ulong RecoveryDeadlineUtcMilliseconds;
    public ulong AtomicGroupId;
    public uint GroupMemberIndex;
    public uint GroupMemberCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAppliedOwnershipDurablePayloadReference
{
    public uint Slot;
    public uint Generation;
    public ulong Length;
    public ulong DigestLow;
    public ulong DigestHigh;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAppliedOwnershipCurrentGrades
{
    public uint ValidMask;
    public uint ReservedUInt32;
    public int ProcessGrade;
    public int CpuGrade;
    public int GpuGrade;
    public int ReservedInt32;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAppliedOwnershipCas
{
    public NativeAppliedOwnershipPrimaryIdentity Primary;
    public ulong JournalInstanceLow;
    public ulong JournalInstanceHigh;
    public NativeAppliedOwnershipActionIdentity OriginalActionIdentity;
    public NativeAppliedOwnershipDurablePayloadReference Payload;
    public ulong ExpectedRecordRevision;
    public NativeAppliedOwnershipCurrentGrades ExpectedCurrentGrades;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAppliedOwnershipRecord
{
    public NativeAppliedOwnershipPrimaryIdentity Primary;
    public NativeAppliedOwnershipOriginalBinding OriginalBinding;
    public NativeAppliedOwnershipDurablePayloadReference Payload;
    public NativeAppliedOwnershipCurrentGrades CurrentGrades;
    public ulong RecordRevision;
    public ulong PromotedAtUtcMilliseconds;
    public ulong UpdatedAtUtcMilliseconds;
    public uint Flags;
    public uint ReservedUInt32;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAppliedOwnershipPromoteInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ExpectedLedgerRevision;
    public NativeAppliedOwnershipPrimaryIdentity Primary;
    public NativeAppliedOwnershipOriginalBinding OriginalBinding;
    public NativeAppliedOwnershipDurablePayloadReference Payload;
    public NativeAppliedOwnershipCurrentGrades CurrentGrades;
    public ulong PromotedAtUtcMilliseconds;
    public fixed ulong Reserved[5];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAppliedOwnershipTransitionInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ExpectedLedgerRevision;
    public NativeAppliedOwnershipCas Cas;
    public NativeAppliedOwnershipOriginalBinding TransitionBinding;
    public NativeAppliedOwnershipDurablePayloadReference TransitionPayload;
    public NativeAppliedOwnershipCurrentGrades NewCurrentGrades;
    public ulong UpdatedAtUtcMilliseconds;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAppliedOwnershipRemoveInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ExpectedLedgerRevision;
    public NativeAppliedOwnershipCas Cas;
    public ulong RemovedAtUtcMilliseconds;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAppliedOwnershipSnapshotHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong LedgerRevision;
    public ulong LedgerInstanceLow;
    public ulong LedgerInstanceHigh;
    public uint EntryCount;
    public uint RecordCapacity;
    public uint PrimaryIndexCapacity;
    public uint PayloadIndexCapacity;
    public ulong MaximumImageBytes;
    public ulong ResidentBytes;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAppliedOwnershipImageHeader
{
    public ulong Magic;
    public uint AbiVersion;
    public uint HeaderSize;
    public uint RecordSize;
    public uint Flags;
    public ulong LedgerInstanceLow;
    public ulong LedgerInstanceHigh;
    public ulong LedgerRevision;
    public uint EntryCount;
    public uint RecordCapacity;
    public ulong ImageLength;
    public ulong Crc64Ecma;
    public uint PrimaryIndexCapacity;
    public uint PayloadIndexCapacity;
    public ulong MaximumImageBytes;
    public fixed ulong Reserved[5];
}
