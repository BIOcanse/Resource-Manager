using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeSamplingSubscriptionAbi
{
    public const uint Version = 0x0003_0000;
}

internal enum NativeSamplingSubscriptionStatus : int
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
internal enum NativeSamplingSubscriptionSourceFlags : uint
{
    None = 0,
    Persistent = 1U << 0,
    ExplicitInterval = 1U << 1,
    Known = Persistent | ExplicitInterval
}

[Flags]
internal enum NativeSamplingSubscriptionConfigurationFlags : ulong
{
    None = 0,
    Known = None
}

[Flags]
internal enum NativeSamplingSubscriptionPlanFlags : ulong
{
    None = 0,
    Active = 1UL << 0,
    DueItems = 1UL << 1,
    NextWakeValid = 1UL << 2,
    ExpiredSources = 1UL << 3,
    Known = Active | DueItems | NextWakeValid | ExpiredSources
}

[Flags]
internal enum NativeSamplingSubscriptionPlanRequestFlags : ulong
{
    None = 0,
    Known = None
}

[Flags]
internal enum NativeSamplingSubscriptionDueItemFlags : uint
{
    None = 0,
    Known = None
}

[Flags]
internal enum NativeSamplingSubscriptionCompletionFlags : ulong
{
    None = 0,
    Active = 1UL << 0,
    NextWakeValid = 1UL << 1,
    Known = Active | NextWakeValid
}

[Flags]
internal enum NativeSamplingSubscriptionTrackValidity : ulong
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
internal enum NativeSamplingSubscriptionRemoveValidity : ulong
{
    None = 0,
    CommandAt = 1UL << 0,
    SourceIdentity = 1UL << 1,
    Required = CommandAt | SourceIdentity,
    Known = Required
}

[Flags]
internal enum NativeSamplingSubscriptionPlanValidity : ulong
{
    None = 0,
    CommandAt = 1UL << 0,
    Required = CommandAt,
    Known = Required
}

[Flags]
internal enum NativeSamplingSubscriptionCompletionValidity : ulong
{
    None = 0,
    CommandAt = 1UL << 0,
    CompletedAt = 1UL << 1,
    Items = 1UL << 2,
    Required = CommandAt | CompletedAt | Items,
    Known = Required
}

[Flags]
internal enum NativeSamplingSubscriptionControlValidity : ulong
{
    None = 0,
    CommandAt = 1UL << 0,
    Required = CommandAt,
    Known = Required
}

internal enum NativeSamplingSubscriptionCompletionStatus : uint
{
    Sampled = 1,
    Failed = 2,
    Skipped = 3
}

internal enum NativeSamplingSubscriptionExpiryReason : uint
{
    TtlElapsed = 1
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSamplingSubscriptionConfiguration
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
internal unsafe struct NativeSamplingSubscriptionCapacity
{
    public uint StructSize;
    public uint SourceCapacity;
    public uint ItemCapacity;
    public uint MembershipCapacity;
    public uint DueItemCapacity;
    public uint SourceViewCapacity;
    public uint ExpiredSourceCapacity;
    public uint SnapshotSourceCapacity;
    public uint SnapshotItemCapacity;
    public uint ReservedU32;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSamplingSubscriptionItemReference
{
    public uint StructSize;
    public uint Flags;
    public ulong ItemHandle;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSamplingSubscriptionTrackInput
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
internal unsafe struct NativeSamplingSubscriptionRemoveInput
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
internal unsafe struct NativeSamplingSubscriptionControlInput
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
internal unsafe struct NativeSamplingSubscriptionPlanHeader
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
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSamplingSubscriptionDueItem
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
internal struct NativeSamplingSubscriptionSourceView
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
internal struct NativeSamplingSubscriptionExpiredSource
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
internal unsafe struct NativeSamplingSubscriptionCompletionInput
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
    public uint Status;
    public uint Flags;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSamplingSubscriptionCompletionOutput
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
internal unsafe struct NativeSamplingSubscriptionSnapshotHeader
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
internal struct NativeSamplingSubscriptionItemState
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
