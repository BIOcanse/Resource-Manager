using System.Runtime.InteropServices;

namespace ResourceManager.Adapter.NativeLedger;

public static class AdapterPrivateResourceLedgerProtocol
{
    public const uint Version = 3;
}

internal enum NativeAdapterResourceLedgerResultCode : int
{
    Ok = 0,
    InvalidArgument = 1,
    InvalidState = 2,
    BufferTooSmall = 3,
    DuplicateResourceKey = 4,
    DuplicateResourceId = 5,
    SequenceRegression = 6,
    TimestampRegression = 7,
    SnapshotTooOld = 8,
    SnapshotFromFuture = 9,
    SameSequenceConflict = 10,
    StaleResourceReference = 11,
    ResourceNotFound = 12,
    ConfigurationRegression = 13,
    InvalidEnum = 14,
    NumericOverflow = 15
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAdapterResourceLedgerConfig
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public uint Capacity;
    public uint MaximumSnapshotAgeMilliseconds;
    public uint MaximumFutureSkewMilliseconds;
    public uint Reserved0;
    public ulong ActivitySettlementInterval;
    public byte ActivityIncrement;
    public byte ActivityDecayNumerator;
    public byte ActivityDecayDenominator;
    public fixed byte Reserved1[5];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAdapterResourceSnapshotInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong OwnerApplicationKey;
    public ulong OwnerProcessInstanceKey;
    public ulong SourceSequence;
    public long CapturedAtUnixMilliseconds;
    public ulong ObservedAtMonotonic;
    public long NowUnixMilliseconds;
    public uint OwnerProcessId;
    public byte SchemaVersion;
    public AdapterSoftwareSurfaceState SurfaceState;
    public byte ImportFlags;
    public fixed byte Reserved0[9];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAdapterResourceInput
{
    public ulong ResourceKey;
    public ulong SizeBytes;
    public uint ResourceId;
    public AdapterResourceTier Tier;
    public AdapterResourceKind ResourceKind;
    public AdapterResourceRecoveryKind RecoveryKind;
    public AdapterResourceGranularity Granularity;
    public AdapterResourceActionMask InapplicableActions;
    public AdapterResourceActionRoute ActionRoute;
    public byte Reserved0;
    public byte ActivityScore;
    public AdapterResourceDemandMask DemandMask;
    public fixed byte Reserved1[3];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAdapterResourceReference
{
    public uint SlotIndex;
    public uint SlotGeneration;
    public ulong ResourceKey;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAdapterResourceActionFeedback
{
    public uint AbiVersion;
    public uint StructSize;
    public NativeAdapterResourceReference Resource;
    public AdapterResourceActionMask Action;
    public AdapterResourceActionStatus Status;
    public AdapterResourceTier CurrentTier;
    public AdapterResourceKind PreferredGpuKind;
    public uint Reserved0;
    public ulong ResidentBytes;
    public ulong ReleasedBytes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAdapterResourceView
{
    public NativeAdapterResourceReference Resource;
    public ulong LedgerGeneration;
    public NativeAdapterResourceInput Value;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAdapterResourceSnapshotSummary
{
    public ulong LedgerGeneration;
    public ulong ConfigurationGeneration;
    public ulong LedgerInstanceId;
    public ulong OwnerApplicationKey;
    public ulong OwnerProcessInstanceKey;
    public ulong SourceSequence;
    public long CapturedAtUnixMilliseconds;
    public ulong ObservedAtMonotonic;
    public ulong Fingerprint;
    public uint OwnerProcessId;
    public uint ResourceCount;
    public AdapterSoftwareSurfaceState SurfaceState;
    public byte HasSnapshot;
    public fixed byte Reserved0[6];
}

internal static unsafe class NativeAdapterResourceLedgerInterop
{
    internal const uint AbiVersion = AdapterPrivateResourceLedgerProtocol.Version;
    private const string LibraryName = "ResourceManager.Adapter.Native";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_private_resource_ledger_abi_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_private_resource_ledger_config_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_private_resource_ledger_snapshot_input_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_private_resource_ledger_resource_input_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_private_resource_ledger_resource_view_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_private_resource_ledger_action_feedback_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_private_resource_ledger_summary_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ulong rm_private_resource_ledger_layout_fingerprint();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_private_resource_ledger_state_alignment();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeAdapterResourceLedgerResultCode rm_private_resource_ledger_state_required_size(
        uint capacity,
        out ulong outputSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeAdapterResourceLedgerResultCode rm_private_resource_ledger_state_initialize(
        void* state,
        ulong stateSize,
        NativeAdapterResourceLedgerConfig* configuration);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeAdapterResourceLedgerResultCode rm_private_resource_ledger_apply_config(
        void* state,
        ulong stateSize,
        NativeAdapterResourceLedgerConfig* configuration);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeAdapterResourceLedgerResultCode rm_private_resource_ledger_import_snapshot(
        void* state,
        ulong stateSize,
        NativeAdapterResourceSnapshotInput* snapshot,
        NativeAdapterResourceInput* resources,
        uint resourceCount,
        NativeAdapterResourceSnapshotSummary* summary);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeAdapterResourceLedgerResultCode rm_private_resource_ledger_touch_resources(
        void* state,
        ulong stateSize,
        uint* resourceIds,
        uint resourceIdCount,
        NativeAdapterResourceSnapshotSummary* summary);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeAdapterResourceLedgerResultCode rm_private_resource_ledger_settle_activity(
        void* state,
        ulong stateSize,
        ulong nowTimestamp,
        NativeAdapterResourceSnapshotSummary* summary);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeAdapterResourceLedgerResultCode rm_private_resource_ledger_apply_action_feedback(
        void* state,
        ulong stateSize,
        NativeAdapterResourceActionFeedback* feedback,
        NativeAdapterResourceSnapshotSummary* summary);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeAdapterResourceLedgerResultCode rm_private_resource_ledger_read_snapshot(
        void* state,
        ulong stateSize,
        NativeAdapterResourceView* output,
        uint outputCapacity,
        NativeAdapterResourceSnapshotSummary* summary);
}
