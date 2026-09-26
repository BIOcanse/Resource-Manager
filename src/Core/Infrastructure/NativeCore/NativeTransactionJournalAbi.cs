using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeTransactionJournalAbi
{
    public const uint Version = 0x0005_0000;
    public const ulong ImageHeaderSize = 128;
}

internal enum NativeTransactionJournalStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    CapacityFull = 3,
    NoData = 4,
    StaleRevision = 5,
    PhaseMismatch = 6,
    InvalidTransition = 7,
    InvalidIdentity = 8,
    InvalidPayload = 9,
    InvalidTime = 10,
    CorruptImage = 11,
    TruncatedImage = 12,
    DuplicateIdentity = 13,
    BufferTooSmall = 14,
    UnknownPhase = 15,
    ChecksumMismatch = 16,
    NonCanonicalImage = 17,
    ConfigurationMismatch = 18,
    RevisionExhausted = 19,
    OutOfMemory = 20
}

internal enum NativeTransactionJournalPhase : uint
{
    Prepared = 1,
    PreviousEffectRestored = 2,
    EffectObserved = 3,
    FeedbackPending = 4,
    ReconciliationPending = 5,
    AuthoritativeResyncPending = 6,
    RecoveryRetryPending = 7,
    RecoveryBlocked = 8,
    EffectInvocationUncertain = 9
}

internal enum NativeTransactionJournalMutationEvent : uint
{
    ConfirmPreviousEffectRestored = 1,
    ConfirmEffectObserved = 2
}

internal enum NativeTransactionJournalRecoveryOutcome : uint
{
    Restored = 1,
    AlreadyRestored = 2,
    OwnershipLost = 3,
    RetryableFailure = 4,
    InvalidProof = 5,
    Unavailable = 6,
    ProcessExited = 7,
    EffectInvocationUncertain = 8,
    AuthoritativeResyncCompleted = 9
}

internal enum NativeTransactionJournalAckResult : uint
{
    Accepted = 1,
    Uncertain = 2,
    Stale = 3,
    Rejected = 4
}

internal enum NativeTransactionJournalFeedbackStatus : uint
{
    Succeeded = 1,
    FailedUnchanged = 2,
    Rejected = 3,
    Skipped = 4,
    OwnershipLost = 5,
    StateUncertain = 6
}

[Flags]
internal enum NativeTransactionJournalFeedbackFlags : uint
{
    None = 0,
    ProcessOwned = 1U << 0,
    CpuOwned = 1U << 1,
    GpuOwned = 1U << 2,
    RollbackPayloadPersisted = 1U << 3,
    MemoryOwned = 1U << 4,
    Known = ProcessOwned | CpuOwned | GpuOwned | RollbackPayloadPersisted | MemoryOwned
}

[Flags]
internal enum NativeTransactionJournalFeedbackValidity : uint
{
    None = 0,
    CompletedAt = 1U << 0,
    ActualProcessGrade = 1U << 1,
    ActualCpuGrade = 1U << 2,
    ActualGpuGrade = 1U << 3,
    ActualMemoryPriority = 1U << 4,
    Required = CompletedAt,
    Known = CompletedAt |
        ActualProcessGrade |
        ActualCpuGrade |
        ActualGpuGrade |
        ActualMemoryPriority
}

[Flags]
internal enum NativeTransactionJournalRecoveryReason : ulong
{
    None = 0,
    Restored = 1UL << 0,
    AlreadyRestored = 1UL << 1,
    OwnershipLost = 1UL << 2,
    RetryableFailure = 1UL << 3,
    InvalidProof = 1UL << 4,
    Unavailable = 1UL << 5,
    ProcessExited = 1UL << 6,
    AckUncertain = 1UL << 7,
    AckStale = 1UL << 8,
    AckRejected = 1UL << 9,
    AcceptedStateUncertain = 1UL << 10,
    AcceptedOwnershipLost = 1UL << 11,
    EffectInvocationUncertain = 1UL << 12,
    RetryBudgetExhausted = 1UL << 13
}

internal enum NativeTransactionJournalPayloadKind : uint
{
    NoneRequired = 1,
    Durable = 2
}

internal enum NativeTransactionJournalScope : uint
{
    Process = 1,
    Software = 2,
    Resource = 3
}

internal enum NativeTransactionJournalDisposition : uint
{
    Apply = 1,
    Restore = 2
}

[Flags]
internal enum NativeTransactionJournalDomain : uint
{
    None = 0,
    Process = 1U << 0,
    Cpu = 1U << 1,
    Gpu = 1U << 2,
    PhysicalMemory = 1U << 3,
    VirtualMemory = 1U << 4,
    VideoMemory = 1U << 5,
    Placement = 1U << 6,
    SharedResource = 1U << 7
}

[Flags]
internal enum NativeTransactionJournalGradeValidity : uint
{
    None = 0,
    Process = 1U << 0,
    Cpu = 1U << 1,
    Gpu = 1U << 2,
    Memory = 1U << 3
}

internal enum NativeTransactionJournalProcessGrade : int
{
    Level4 = -4,
    Level3 = -3,
    Level2 = -2,
    Level1 = -1,
    Normal = 0,
    A1 = 1
}

internal enum NativeTransactionJournalAdapterGrade : int
{
    Freeze = 0,
    Optimize = 1,
    Normal = 2,
    Extreme = 3
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTransactionJournalCreateConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong JournalInstanceLow;
    public ulong JournalInstanceHigh;
    public uint RecordCapacity;
    public uint Flags;
    public ulong MaximumResidentBytes;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTransactionJournalOpenConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public uint MaximumRecordCapacity;
    public uint Flags;
    public ulong MaximumResidentBytes;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTransactionJournalCapacity
{
    public uint StructSize;
    public uint RecordCapacity;
    public ulong MaximumImageLength;
    public ulong ResidentBytes;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTransactionJournalIdentity
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
internal unsafe struct NativeTransactionJournalPrepareInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ExpectedJournalRevision;
    public NativeTransactionJournalIdentity Identity;
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
    public uint PayloadKind;
    public uint PayloadSlot;
    public uint PayloadGeneration;
    public uint PayloadReserved;
    public ulong PayloadLength;
    public ulong PayloadDigestLow;
    public ulong PayloadDigestHigh;
    public ulong NowUtcMilliseconds;
    public uint MaximumRecoveryAttempts;
    public uint RetryPolicyReserved;
    public ulong RecoveryDeadlineUtcMilliseconds;
    public ulong AtomicGroupId;
    public uint GroupMemberIndex;
    public uint GroupMemberCount;
    public ulong PayloadProvenanceDigestLow;
    public ulong PayloadProvenanceDigestHigh;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTransactionJournalPrepareBatchInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ExpectedJournalRevision;
    public uint InputCount;
    public uint Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTransactionJournalMutationInput
{
    public uint AbiVersion;
    public uint StructSize;
    public NativeTransactionJournalIdentity Identity;
    public ulong ExpectedJournalRevision;
    public ulong ExpectedEntryRevision;
    public ulong NowUtcMilliseconds;
    public uint Event;
    public uint ExpectedPhase;
    public uint StableSystemStatus;
    public uint StableSystemError;
    public fixed ulong Reserved[6];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTransactionJournalStageFeedbackInput
{
    public uint AbiVersion;
    public uint StructSize;
    public NativeTransactionJournalIdentity Identity;
    public ulong ExpectedJournalRevision;
    public ulong ExpectedEntryRevision;
    public ulong CompletedAtUtcMilliseconds;
    public uint ExpectedPhase;
    public uint FeedbackValidMask;
    public uint FeedbackFlags;
    public uint FeedbackStatus;
    public uint FeedbackSystemStatus;
    public uint FeedbackSystemError;
    public uint FeedbackReserved;
    public int ActualProcessGrade;
    public int ActualCpuGrade;
    public int ActualGpuGrade;
    public uint ActualReserved;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTransactionJournalRecoveryEvidenceInput
{
    public uint AbiVersion;
    public uint StructSize;
    public NativeTransactionJournalIdentity Identity;
    public ulong ExpectedJournalRevision;
    public ulong ExpectedEntryRevision;
    public ulong ObservedAtUtcMilliseconds;
    public ulong RetryNotBeforeUtcMilliseconds;
    public uint Outcome;
    public uint ExpectedPhase;
    public uint StableSystemStatus;
    public uint StableSystemError;
    public uint Flags;
    public uint EvidenceReserved;
    public ulong AuthoritativeFactsGeneration;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTransactionJournalAckInput
{
    public uint AbiVersion;
    public uint StructSize;
    public NativeTransactionJournalIdentity Identity;
    public ulong ExpectedJournalRevision;
    public ulong ExpectedEntryRevision;
    public ulong AcknowledgedAtUtcMilliseconds;
    public ulong RetryNotBeforeUtcMilliseconds;
    public uint Result;
    public uint ExpectedPhase;
    public uint StableSystemStatus;
    public uint StableSystemError;
    public uint Flags;
    public uint AckReserved;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTransactionJournalRecord
{
    public NativeTransactionJournalIdentity Identity;
    public uint Scope;
    public uint Disposition;
    public uint DomainMask;
    public uint Phase;
    public uint GradeValidMask;
    public uint GradeReserved;
    public uint StableSystemStatus;
    public uint StableSystemError;
    public int ProcessFromGrade;
    public int ProcessToGrade;
    public int CpuFromGrade;
    public int CpuToGrade;
    public int GpuFromGrade;
    public int GpuToGrade;
    public uint PayloadKind;
    public uint PayloadSlot;
    public uint PayloadGeneration;
    public uint PayloadReserved;
    public ulong PayloadLength;
    public ulong PayloadDigestLow;
    public ulong PayloadDigestHigh;
    public ulong PreparedAtUtcMilliseconds;
    public ulong UpdatedAtUtcMilliseconds;
    public ulong RetryNotBeforeUtcMilliseconds;
    public ulong EntryRevision;
    public uint FeedbackValidMask;
    public uint FeedbackFlags;
    public uint FeedbackStatus;
    public uint FeedbackSystemStatus;
    public uint FeedbackSystemError;
    public uint FeedbackReserved;
    public ulong FeedbackCompletedAtUtcMilliseconds;
    public int ActualProcessGrade;
    public int ActualCpuGrade;
    public int ActualGpuGrade;
    public uint ActualReserved;
    public ulong RecoveryReasonMask;
    public uint RetryAttemptCount;
    public uint MaximumRecoveryAttempts;
    public ulong RecoveryDeadlineUtcMilliseconds;
    public ulong AtomicGroupId;
    public uint GroupMemberIndex;
    public uint GroupMemberCount;
    public ulong PayloadProvenanceDigestLow;
    public ulong PayloadProvenanceDigestHigh;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTransactionJournalSnapshotHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong JournalRevision;
    public ulong JournalInstanceLow;
    public ulong JournalInstanceHigh;
    public uint EntryCount;
    public uint Capacity;
    public uint PreparedCount;
    public uint PreviousEffectRestoredCount;
    public uint EffectObservedCount;
    public uint FeedbackPendingCount;
    public uint ReconciliationPendingCount;
    public uint AuthoritativeResyncPendingCount;
    public uint RecoveryRetryPendingCount;
    public uint RecoveryBlockedCount;
    public uint EffectInvocationUncertainCount;
    public uint SnapshotReserved;
    public fixed ulong Reserved[6];
}
