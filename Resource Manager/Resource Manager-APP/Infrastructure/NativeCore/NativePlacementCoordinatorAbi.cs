using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativePlacementCoordinatorAbi
{
    public const uint Version = 0x0001_0000;
}

internal enum NativePlacementCoordinatorStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    Unavailable = 3,
    NoData = 4,
    BufferTooSmall = 5,
    StaleFrame = 6,
    OutOfMemory = 7,
    NativeError = 8
}

internal enum NativePlacementResourceKind : uint
{
    Unknown = 0,
    Cpu = 1,
    Gpu = 2
}

internal enum NativePlacementKind : uint
{
    Unknown = 0,
    CpuSets = 1,
    CpuAffinity = 2,
    ProcessPriority = 3,
    ProcessMemoryPriority = 4,
    GpuPreference = 5,
    GpuRuntimeRebuild = 6,
    GpuShimPolicy = 7
}

internal enum NativePlacementObservationStatus : uint
{
    Found = 1,
    NotFoundOrExited = 2,
    Unavailable = 3,
    Unchecked = 4
}

internal enum NativePlacementActionDisposition : uint
{
    Apply = 1,
    Restore = 2
}

internal enum NativePlacementFeedbackStatus : uint
{
    Applied = 1,
    Restored = 2,
    AlreadySatisfied = 3,
    OwnershipLost = 4,
    RetryableFailure = 5,
    InvalidReceipt = 6
}

internal enum NativePlacementSlotState : uint
{
    Empty = 0,
    Applied = 1,
    PendingApply = 2,
    PendingRestore = 3,
    RetryWait = 4,
    Blocked = 5
}

[Flags]
internal enum NativePlacementCycleValidity : ulong
{
    None = 0,
    ObservedAt = 1UL << 0,
    DesiredComplete = 1UL << 1,
    AppliedComplete = 1UL << 2,
    Required = ObservedAt | DesiredComplete | AppliedComplete
}

[Flags]
internal enum NativePlacementDesiredValidity : ulong
{
    None = 0,
    Identity = 1UL << 0,
    DesiredDigest = 1UL << 1,
    ProcessIdentity = 1UL << 2,
    Required = Identity | DesiredDigest,
    Known = Required | ProcessIdentity
}

[Flags]
internal enum NativePlacementDesiredFlags : uint
{
    None = 0,
    OverrideExternalState = 1U << 0
}

[Flags]
internal enum NativePlacementAppliedValidity : ulong
{
    None = 0,
    Identity = 1UL << 0,
    ReceiptDigest = 1UL << 1,
    PreviousDigest = 1UL << 2,
    CurrentDigest = 1UL << 3,
    ProcessIdentity = 1UL << 4,
    Required = Identity | ReceiptDigest | PreviousDigest,
    Known = Required | CurrentDigest | ProcessIdentity
}

[Flags]
internal enum NativePlacementAppliedFlags : uint
{
    None = 0,
    PayloadValid = 1U << 0
}

[Flags]
internal enum NativePlacementFeedbackValidity : ulong
{
    None = 0,
    ObservedDigest = 1UL << 0
}

[Flags]
internal enum NativePlacementActionFlags : uint
{
    None = 0,
    ProcessIdentityValid = 1U << 0,
    Known = ProcessIdentityValid
}

[Flags]
internal enum NativePlacementActionReason : ulong
{
    None = 0,
    DesiredMissing = 1UL << 0,
    DesiredChanged = 1UL << 1,
    ReceiptMissing = 1UL << 2,
    RetryDue = 1UL << 3,
    AlreadyRestored = 1UL << 4,
    OwnershipLost = 1UL << 5,
    ObservationUnavailable = 1UL << 6,
    InvalidReceipt = 1UL << 7,
    Known = DesiredMissing
        | DesiredChanged
        | ReceiptMissing
        | RetryDue
        | AlreadyRestored
        | OwnershipLost
        | ObservationUnavailable
        | InvalidReceipt
}

[Flags]
internal enum NativePlacementSnapshotFlags : ulong
{
    None = 0,
    NextWakeValid = 1UL << 0
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePlacementCoordinatorConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public uint MaximumDesiredCount;
    public uint MaximumAppliedCount;
    public uint MaximumActionCount;
    public uint MaximumStateCount;
    public ulong RetryDelayMilliseconds;
    public ulong ActionTimeoutMilliseconds;
    public ulong MaximumFutureSkewMilliseconds;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePlacementCoordinatorCapacity
{
    public uint StructSize;
    public uint StateCapacity;
    public uint ActionCapacity;
    public uint SnapshotStateCapacity;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePlacementCoordinatorCycleInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CycleEpoch;
    public ulong ObservedAtMilliseconds;
    public ulong ValidMask;
    public uint DesiredCount;
    public uint AppliedCount;
    public uint ActionCapacity;
    public uint ActionCount;
    public ulong NextWakeMilliseconds;
    public ulong StateRevision;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePlacementDesiredInput
{
    public uint StructSize;
    public uint Flags;
    public ulong TargetKey;
    public ulong RecordKey;
    public uint ResourceKind;
    public uint PlacementKind;
    public ulong DesiredDigest;
    public ulong ProcessStartKey;
    public uint ProcessId;
    public uint Priority;
    public ulong ValidMask;
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePlacementAppliedInput
{
    public uint StructSize;
    public uint Flags;
    public ulong TargetKey;
    public ulong RecordKey;
    public uint ResourceKind;
    public uint PlacementKind;
    public ulong ReceiptDigest;
    public ulong PreviousDigest;
    public ulong CurrentDigest;
    public ulong ProcessStartKey;
    public uint ProcessId;
    public uint ObservationStatus;
    public ulong ValidMask;
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePlacementAction
{
    public uint StructSize;
    public uint Disposition;
    public ulong ActionId;
    public ulong TargetKey;
    public ulong RecordKey;
    public uint ResourceKind;
    public uint PlacementKind;
    public ulong DesiredDigest;
    public ulong PreviousDigest;
    public ulong ProcessStartKey;
    public uint ProcessId;
    public uint Flags;
    public ulong ReasonMask;
    public ulong DeadlineMilliseconds;
    public uint Priority;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePlacementFeedback
{
    public uint StructSize;
    public uint Status;
    public ulong ActionId;
    public ulong TargetKey;
    public ulong RecordKey;
    public ulong CompletedAtMilliseconds;
    public uint SystemErrorCode;
    public uint Flags;
    public ulong ObservedDigest;
    public ulong ValidMask;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePlacementSnapshotHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong StateRevision;
    public ulong LastCycleEpoch;
    public ulong NextActionId;
    public uint ActiveStateCount;
    public uint PendingApplyCount;
    public uint PendingRestoreCount;
    public uint BlockedCount;
    public uint RetryWaitCount;
    public uint StateOutputCount;
    public ulong Flags;
    public ulong NextWakeMilliseconds;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePlacementState
{
    public uint StructSize;
    public uint State;
    public ulong TargetKey;
    public ulong RecordKey;
    public uint ResourceKind;
    public uint PlacementKind;
    public ulong DesiredDigest;
    public ulong AppliedDigest;
    public ulong PreviousDigest;
    public ulong PendingActionId;
    public ulong RetryAtMilliseconds;
    public ulong LastCycleEpoch;
    public ulong ProcessStartKey;
    public uint ProcessId;
    public uint RetryCount;
    public fixed ulong Reserved[2];
}
