using System.Runtime.InteropServices;

namespace ResourceManager.Adapter.SharedMemory;

internal enum NativeSharedResourceResult : int
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    MappingTooSmall = 3,
    InvalidMapping = 4,
    CapacityExhausted = 5,
    NotFound = 6,
    StaleReference = 7,
    ResourceBusy = 8,
    QueueEmpty = 9,
    AccessDenied = 10,
    Expired = 11,
    InvalidState = 12,
    InconsistentState = 13,
    SynchronizationFailed = 14,
    BufferTooSmall = 15,
    Conflict = 16
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLedgerConfig
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong LedgerInstanceId;
    public ulong OwnerApplicationKey;
    public long OwnerProcessCreatedUtcTicks;
    public double SubscriptionCoefficient;
    public uint ResourceCapacity;
    public uint SubscriptionCapacity;
    public uint TaskCapacity;
    public int OwnerProcessId;
    public byte SynchronizationKind;
    public fixed byte Reserved0[3];
    public uint LeaseClockDomain;
    public ulong LeaseClockFrequencyHz;
    public ulong MaximumSubscriptionTtl;
    public ulong MaximumQueueTtl;
    public ulong MaximumGrantTtl;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeResourceRef
{
    public ulong LedgerInstanceId;
    public ulong ResourceGeneration;
    public ulong PublicResourceId;
    public uint ResourceSlot;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeResourcePublication
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong PublicResourceId;
    public ulong OwnerApplicationKey;
    public ulong OwnerInstanceIdLow;
    public ulong OwnerInstanceIdHigh;
    public ulong OwnerContextGeneration;
    public ulong LeaseGeneration;
    public ulong BindingGeneration;
    public ulong CapabilityGeneration;
    public ulong ExecutorIdLow;
    public ulong ExecutorIdHigh;
    public ulong ResourceKey;
    public ulong AdapterKey;
    public ulong SizeBytes;
    public ulong ContentIdentityHash;
    public ulong PayloadMappingId;
    public ulong PayloadGeneration;
    public long LastUpdatedUtcTicks;
    public int OwnerProcessId;
    public uint ResourceId;
    public ushort MaxParallelGrants;
    public byte Tier;
    public byte ResourceKind;
    public byte RecoveryKind;
    public byte Granularity;
    public byte InapplicableActions;
    public byte ActionRoute;
    public byte OwnerDemandMask;
    public byte ActivityScore;
    public byte SurfaceState;
    public byte Availability;
    public byte Flags;
    public byte Reserved0;
    public uint Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeResourceSnapshot
{
    public uint AbiVersion;
    public uint StructSize;
    public NativeResourceRef Id;
    public ulong OwnerApplicationKey;
    public ulong OwnerInstanceIdLow;
    public ulong OwnerInstanceIdHigh;
    public ulong OwnerContextGeneration;
    public ulong LeaseGeneration;
    public ulong BindingGeneration;
    public ulong CapabilityGeneration;
    public ulong ExecutorIdLow;
    public ulong ExecutorIdHigh;
    public ulong ResourceKey;
    public ulong AdapterKey;
    public ulong SizeBytes;
    public ulong ContentIdentityHash;
    public ulong PayloadMappingId;
    public ulong PayloadGeneration;
    public long LastUpdatedUtcTicks;
    public double SubscriptionMultiplier;
    public int OwnerProcessId;
    public uint ResourceId;
    public uint ActiveSubscriberCount;
    public uint QueuedRequestCount;
    public uint ReservedGrantCount;
    public uint ActiveUseCount;
    public ulong SchedulingRevision;
    public ulong GateEpoch;
    public ushort MaxParallelGrants;
    public byte Tier;
    public byte ResourceKind;
    public byte RecoveryKind;
    public byte Granularity;
    public byte InapplicableActions;
    public byte ActionRoute;
    public byte OwnerDemandMask;
    public byte SubscriptionIntentMask;
    public byte ActivityScore;
    public byte SurfaceState;
    public byte Availability;
    public byte Flags;
    public byte AllowedActions;
    public byte ConsistencyState;
    public byte DestructiveActive;
    public byte Reserved0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSubscriptionRequest
{
    public uint AbiVersion;
    public uint StructSize;
    public NativeResourceRef Resource;
    public ulong SubscriberApplicationKey;
    public ulong SubscriberInstanceId;
    public ulong SubscriberSessionId;
    public ulong LeaseDuration;
    public int SubscriberProcessId;
    public byte IntentMask;
    public byte Reserved00;
    public byte Reserved01;
    public byte Reserved02;
    public uint Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSubscriptionReceipt
{
    public NativeResourceRef Resource;
    public ulong SubscriberInstanceId;
    public ulong SubscriberSessionId;
    public ulong DeadlineTimestamp;
    public ulong SubscriptionGeneration;
    public uint SubscriptionSlot;
    public int SubscriberProcessId;
    public byte IntentMask;
    public byte Reserved00;
    public byte Reserved01;
    public byte Reserved02;
    public byte Reserved03;
    public byte Reserved04;
    public byte Reserved05;
    public byte Reserved06;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeUseRequest
{
    public uint AbiVersion;
    public uint StructSize;
    public NativeResourceRef Resource;
    public ulong RequestKey;
    public ulong RequesterApplicationKey;
    public ulong RequesterInstanceId;
    public ulong RequesterSessionId;
    public ulong ScorePlanGeneration;
    public ulong QueueLeaseDuration;
    public double BaseScore;
    public int RequesterProcessId;
    public uint Reserved0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTaskReceipt
{
    public NativeResourceRef Resource;
    public ulong RequestKey;
    public ulong RequesterApplicationKey;
    public ulong RequesterInstanceId;
    public ulong RequesterSessionId;
    public ulong TaskGeneration;
    public uint TaskSlot;
    public byte State;
    public byte Reserved0;
    public ushort Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeGrantReceipt
{
    public NativeTaskReceipt Task;
    public ulong GrantGeneration;
    public ulong PayloadGeneration;
    public ulong DeadlineTimestamp;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDestructiveExpectedRequest
{
    public uint AbiVersion;
    public uint StructSize;
    public NativeResourceRef Resource;
    public ulong SchedulingRevision;
    public ulong ActionAttemptIdLow;
    public ulong ActionAttemptIdHigh;
    public byte ActionMask;
    public byte Reserved00;
    public byte Reserved01;
    public byte Reserved02;
    public byte Reserved03;
    public byte Reserved04;
    public byte Reserved05;
    public byte Reserved06;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDestructiveToken
{
    public NativeResourceRef Resource;
    public ulong SchedulingRevision;
    public ulong GateEpoch;
    public ulong ActionAttemptIdLow;
    public ulong ActionAttemptIdHigh;
    public byte ActionMask;
    public byte Reserved00;
    public byte Reserved01;
    public byte Reserved02;
    public byte Reserved03;
    public byte Reserved04;
    public byte Reserved05;
    public byte Reserved06;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRecallExpectedRequest
{
    public uint AbiVersion;
    public uint StructSize;
    public NativeResourceRef Resource;
    public ulong SchedulingRevision;
    public ulong GateEpoch;
    public ulong ActionAttemptIdLow;
    public ulong ActionAttemptIdHigh;
    public ulong PayloadGeneration;
    public uint SubscriberCount;
    public byte ActivityScore;
    public byte Flags;
    public byte Reserved00;
    public byte Reserved01;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRecallToken
{
    public NativeResourceRef Resource;
    public ulong SchedulingRevision;
    public ulong GateEpoch;
    public ulong ActionAttemptIdLow;
    public ulong ActionAttemptIdHigh;
    public ulong PayloadGeneration;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDestructiveNoEffectReceipt
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ActionAttemptIdLow;
    public ulong ActionAttemptIdHigh;
    public ulong ReceiptIdLow;
    public ulong ReceiptIdHigh;
    public ulong ObservedMonotonicTimestamp;
    public byte ProofMask;
    public byte Reason;
    public byte Reserved00;
    public byte Reserved01;
    public byte Reserved02;
    public byte Reserved03;
    public byte Reserved04;
    public byte Reserved05;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeHostPublicManagerPlanRequest
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong SampleGeneration;
    public byte MemoryShortage;
    public byte VideoMemoryShortage;
    public fixed byte Reserved0[6];
    public uint SubscriberWeight;
    public uint ActivityWeight;
    public uint WeightScale;
    public uint SubscriberHalfSaturation;
    public uint ActivityHalfSaturation;
    public uint Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeHostPublicUnloadCandidate
{
    public NativeResourceRef Resource;
    public ulong SchedulingRevision;
    public ulong GateEpoch;
    public ulong SizeBytes;
    public uint RetentionScoreQ16;
    public uint SubscriberCount;
    public byte ActivityScore;
    public byte Reason;
    public byte Flags;
    public byte Reserved0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeHostPublicManagerPlanSummary
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong TopologyGeneration;
    public ulong SampleGeneration;
    public uint ResourceCount;
    public uint ProtectedResourceCount;
    public uint CandidateCount;
    public uint ZeroSubscriberCandidateCount;
    public uint CapacityCandidateCount;
    public uint Reserved0;
}

internal static unsafe class NativeSharedResourceMethods
{
    internal const string LibraryName = "ResourceManager.Adapter.Native";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_shared_ledger_abi_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ulong rm_shared_ledger_capabilities();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ulong rm_shared_resource_layout_fingerprint();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_ledger_required_size(
        NativeLedgerConfig* config,
        ulong* outputSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_ledger_create(
        void* mapping,
        ulong mappingSize,
        void* windowsMutexHandle,
        NativeLedgerConfig* config,
        IntPtr* outputHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_ledger_open(
        void* mapping,
        ulong mappingSize,
        void* windowsMutexHandle,
        NativeLedgerConfig* config,
        byte writable,
        IntPtr* outputHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void rm_shared_ledger_destroy(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_resource_publish(
        IntPtr handle,
        NativeResourcePublication* publication,
        NativeResourceRef* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_resource_update(
        IntPtr handle,
        NativeResourceRef* resource,
        NativeResourcePublication* publication);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_resource_revoke(IntPtr handle, NativeResourceRef* resource);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_resource_snapshot(
        IntPtr handle,
        NativeResourceRef* resource,
        NativeResourceSnapshot* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_resource_snapshot_by_public_id(
        IntPtr handle,
        ulong publicResourceId,
        NativeResourceSnapshot* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_resource_snapshot_batch(
        IntPtr handle,
        NativeResourceSnapshot* output,
        uint capacity,
        uint* outputCount,
        ulong* topologyGeneration);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_subscription_subscribe(
        IntPtr handle,
        NativeSubscriptionRequest* request,
        NativeSubscriptionReceipt* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_subscription_confirm(
        IntPtr handle,
        NativeSubscriptionReceipt* receipt,
        ulong leaseDuration,
        byte intentMask,
        NativeSubscriptionReceipt* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_subscription_release(
        IntPtr handle,
        NativeSubscriptionReceipt* receipt);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_subscription_sweep(
        IntPtr handle,
        uint* expiredCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_use_enqueue(
        IntPtr handle,
        NativeUseRequest* request,
        NativeTaskReceipt* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_use_reserve_next(
        IntPtr handle,
        NativeResourceRef* resource,
        ulong grantLeaseDuration,
        NativeGrantReceipt* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_use_begin(
        IntPtr handle,
        NativeGrantReceipt* receipt,
        NativeGrantReceipt* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_use_confirm(
        IntPtr handle,
        NativeGrantReceipt* receipt,
        ulong leaseDuration,
        NativeGrantReceipt* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_use_complete(IntPtr handle, NativeGrantReceipt* receipt);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_use_cancel(IntPtr handle, NativeTaskReceipt* receipt);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_use_sweep(
        IntPtr handle,
        uint* removedCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_action_filter(
        IntPtr handle,
        NativeResourceRef* resource,
        byte requestedActions,
        byte* allowedActions);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_action_begin_destructive_expected(
        IntPtr handle,
        NativeDestructiveExpectedRequest* request,
        NativeDestructiveToken* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_action_commit_destructive(
        IntPtr handle,
        NativeDestructiveToken* token,
        NativeResourcePublication* postState);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_action_abort_destructive(
        IntPtr handle,
        NativeDestructiveToken* token,
        NativeDestructiveNoEffectReceipt* receipt);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_action_begin_recall_expected(
        IntPtr handle,
        NativeRecallExpectedRequest* request,
        NativeRecallToken* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_action_commit_recall(
        IntPtr handle,
        NativeRecallToken* token,
        NativeResourcePublication* postState);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_action_abort_recall(
        IntPtr handle,
        NativeRecallToken* token,
        NativeDestructiveNoEffectReceipt* receipt);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_shared_public_manager_plan(
        IntPtr handle,
        NativeHostPublicManagerPlanRequest* request,
        NativeHostPublicUnloadCandidate* output,
        uint outputCapacity,
        NativeHostPublicManagerPlanSummary* summary);
}
