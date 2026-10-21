using System.Runtime.InteropServices;

namespace ResourceManager.Subscription.Native;

public static class NativeSamplingSubscriptionAbi
{
    public const uint Version = 0x0004_0000;
}

public enum NativeSamplingSubscriptionStatus : int
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

[Flags]
public enum NativeSamplingSubscriptionSourceFlags : uint
{
    None = 0,
    Persistent = 1U << 0,
    ExplicitInterval = 1U << 1,
    FreshOnTrigger = 1U << 2,
    AlignSameItemPhase = 1U << 3,
    PeriodicTriggers = 1U << 4,
    Known = Persistent | ExplicitInterval | FreshOnTrigger | AlignSameItemPhase | PeriodicTriggers
}

[Flags]
public enum NativeSamplingSubscriptionConfigurationFlags : ulong
{
    None = 0,
    Known = None
}

[Flags]
public enum NativeSamplingSubscriptionPlanFlags : ulong
{
    None = 0,
    Active = 1UL << 0,
    DueItems = 1UL << 1,
    NextWakeValid = 1UL << 2,
    ExpiredSources = 1UL << 3,
    Triggers = 1UL << 4,
    Known = Active | DueItems | NextWakeValid | ExpiredSources | Triggers
}

[Flags]
public enum NativeSamplingSubscriptionPlanRequestFlags : ulong
{
    None = 0,
    Known = None
}

[Flags]
public enum NativeSamplingSubscriptionDueItemFlags : uint
{
    None = 0,
    Known = None
}

[Flags]
public enum NativeSamplingSubscriptionTriggerFlags : uint
{
    None = 0,
    Fresh = 1U << 0,
    Known = Fresh
}

[Flags]
public enum NativeSamplingSubscriptionCompletionFlags : ulong
{
    None = 0,
    Active = 1UL << 0,
    NextWakeValid = 1UL << 1,
    Known = Active | NextWakeValid
}

[Flags]
public enum NativeSamplingSubscriptionTrackValidity : ulong
{
    None = 0,
    CommandAt = 1UL << 0,
    ObservedAt = 1UL << 1,
    SourceIdentity = 1UL << 2,
    Items = 1UL << 3,
    ExplicitInterval = 1UL << 4,
    Required = CommandAt | ObservedAt | SourceIdentity | Items,
    Known = Required | ExplicitInterval
}

[Flags]
public enum NativeSamplingSubscriptionRemoveValidity : ulong
{
    None = 0,
    CommandAt = 1UL << 0,
    SourceIdentity = 1UL << 1,
    Required = CommandAt | SourceIdentity,
    Known = Required
}

[Flags]
public enum NativeSamplingSubscriptionPlanValidity : ulong
{
    None = 0,
    CommandAt = 1UL << 0,
    Required = CommandAt,
    Known = Required
}

[Flags]
public enum NativeSamplingSubscriptionCompletionValidity : ulong
{
    None = 0,
    CommandAt = 1UL << 0,
    CompletedAt = 1UL << 1,
    Items = 1UL << 2,
    Triggers = 1UL << 3,
    Required = CommandAt | CompletedAt | Items | Triggers,
    Known = Required
}

[Flags]
public enum NativeSamplingSubscriptionControlValidity : ulong
{
    None = 0,
    CommandAt = 1UL << 0,
    Required = CommandAt,
    Known = Required
}

public enum NativeSamplingSubscriptionCompletionStatus : uint
{
    Sampled = 1,
    Failed = 2,
    Skipped = 3
}

public enum NativeSamplingSubscriptionExpiryReason : uint
{
    TtlElapsed = 1
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSamplingSubscriptionConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public uint MaximumSourceCount;
    public uint MaximumItemCount;
    public uint MaximumMembershipCount;
    public uint MaximumDueItemCount;
    public uint MaximumSourceViewCount;
    public uint MaximumExpiredSourceCount;
    public ulong DefaultIntervalMilliseconds;
    public ulong MinimumIntervalMilliseconds;
    public ulong ActiveTtlMilliseconds;
    public ulong MaximumFutureSkewMilliseconds;
    public ulong Flags;
    public ulong ReservedU64_0;
    public ulong ReservedU64_1;
    public ulong ReservedU64_2;
    public uint ReservedU32_0;
    public uint ReservedU32_1;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSamplingSubscriptionCapacity
{
    public uint StructSize;
    public uint SourceCapacity;
    public uint ItemCapacity;
    public uint MembershipCapacity;
    public uint DueItemCapacity;
    public uint SourceViewCapacity;
    public uint ExpiredSourceCapacity;
    public uint TriggerCapacity;
    public uint SnapshotSourceCapacity;
    public uint SnapshotItemCapacity;
    public uint ReservedU32;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSamplingSubscriptionItemReference
{
    public uint StructSize;
    public uint Flags;
    public ulong ItemHandle;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSamplingSubscriptionTrackInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong CommandAtMilliseconds;
    public ulong ObservedAtMilliseconds;
    public ulong SourceHandle;
    public ulong ExplicitIntervalMilliseconds;
    public ulong ValidMask;
    public uint ItemCount;
    public uint Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSamplingSubscriptionRemoveInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong CommandAtMilliseconds;
    public ulong SourceHandle;
    public ulong ValidMask;
    public uint Flags;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSamplingSubscriptionControlInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong CommandAtMilliseconds;
    public ulong ValidMask;
    public uint Flags;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSamplingSubscriptionPlanHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong PlanEpoch;
    public ulong CommandAtMilliseconds;
    public ulong ValidMask;
    public uint DueItemCapacity;
    public uint SourceViewCapacity;
    public uint ExpiredSourceCapacity;
    public uint DueItemCount;
    public uint SourceViewCount;
    public uint ExpiredSourceCount;
    public uint ActiveSourceCount;
    public uint ActiveItemCount;
    public ulong NextWakeMilliseconds;
    public ulong StateRevision;
    public ulong Flags;
    public ulong RequestFlags;
    public uint TriggerCapacity;
    public uint TriggerCount;
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeSamplingSubscriptionTrigger
{
    public uint StructSize;
    public uint Flags;
    public ulong SourceHandle;
    public ulong ItemHandle;
    public ulong ScheduledDueMilliseconds;
    public ulong Sequence;
    public ulong PlanEpoch;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSamplingSubscriptionDueItem
{
    public uint StructSize;
    public uint Flags;
    public ulong ItemHandle;
    public ulong EffectiveIntervalMilliseconds;
    public ulong LastSampledMilliseconds;
    public ulong RetryAtMilliseconds;
    public ulong PlanEpoch;
    public uint SourceCount;
    public uint ReservedU32;
    public ulong ScheduledDueMilliseconds;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeSamplingSubscriptionSourceView
{
    public uint StructSize;
    public uint Flags;
    public ulong SourceHandle;
    public ulong LastSeenMilliseconds;
    public ulong ObservedIntervalMilliseconds;
    public ulong EffectiveIntervalMilliseconds;
    public ulong ExplicitIntervalMilliseconds;
    public uint ItemCount;
    public uint ReservedU32;
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeSamplingSubscriptionExpiredSource
{
    public uint StructSize;
    public uint Reason;
    public ulong SourceHandle;
    public ulong LastSeenMilliseconds;
    public uint SourceFlags;
    public uint ItemCount;
    public ulong ExpiredAtMilliseconds;
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSamplingSubscriptionCompletionInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong PlanEpoch;
    public ulong CommandAtMilliseconds;
    public ulong CompletedAtMilliseconds;
    public ulong ValidMask;
    public uint ItemCount;
    public uint TriggerCount;
    public uint Status;
    public uint Flags;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSamplingSubscriptionCompletionOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong StateRevision;
    public ulong OperationEpoch;
    public ulong NextWakeMilliseconds;
    public uint ActiveSourceCount;
    public uint ActiveItemCount;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeSamplingSubscriptionSnapshotHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong StateRevision;
    public ulong LastOperationEpoch;
    public ulong LastCommandAtMilliseconds;
    public ulong LastPlanEpoch;
    public ulong NextWakeMilliseconds;
    public uint ActiveSourceCount;
    public uint KnownItemCount;
    public uint MembershipCount;
    public uint PersistentSourceCount;
    public uint ExplicitIntervalSourceCount;
    public uint SourceOutputCount;
    public uint ItemOutputCount;
    public uint ReservedU32;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeSamplingSubscriptionItemState
{
    public uint StructSize;
    public uint Flags;
    public ulong ItemHandle;
    public ulong LastSampledMilliseconds;
    public ulong RetryAtMilliseconds;
    public ulong EffectiveIntervalMilliseconds;
    public ulong LastPlannedEpoch;
    public uint SourceCount;
    public uint ReservedU32;
    public ulong ScheduledDueMilliseconds;
}
