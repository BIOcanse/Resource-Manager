using System.Runtime.InteropServices;
using ResourceManager.Adapter.SharedMemory;

namespace ResourceManager.Adapter.LocalResources;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLocalResourceId
{
    internal ulong Low;
    internal ulong High;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourceManagerConfig
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong ConfigurationGeneration;
    internal uint TableCapacity;
    internal uint ResourceCapacity;
    internal uint CapabilityCapacity;
    internal uint PendingCapacity;
    internal uint UidBucketCapacity;
    internal uint PartitionCapacity;
    internal uint MaximumConcurrentTables;
    internal uint SmoothReleaseIntervalEpochs;
    internal uint ActivityDecayNumerator;
    internal uint ActivityDecayDenominator;
    internal uint ConcentratedTriggerFreePercent;
    internal uint ConcentratedTargetFreePercent;
    internal uint SmoothTriggerFreePercent;
    internal uint SmoothEmergencyFreePercent;
    internal uint SmoothMaximumReleasesPerInterval;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourceTableSpec
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal NativeLocalResourceId TableId;
    internal ulong TableIncarnation;
    internal NativeLocalResourceId AdapterKey;
    internal ulong TopologyGeneration;
    internal uint Capacity;
    internal uint PartitionReservationStart;
    internal uint PartitionReservationCapacity;
    internal byte Domain;
    internal fixed byte Reserved0[3];
    internal ulong Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLocalResourceTableHandle
{
    internal ulong ManagerInstanceId;
    internal ulong SlotGeneration;
    internal NativeLocalResourceId TableId;
    internal ulong TableIncarnation;
    internal uint SlotIndex;
    internal uint Reserved0;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourceCapabilitySpec
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong CapabilityId;
    internal uint ActionCode;
    internal byte ExpectedEffects;
    internal byte Destructive;
    internal fixed byte Reserved0[2];
    internal ulong Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLocalResourceCapabilityHandle
{
    internal ulong ManagerInstanceId;
    internal ulong SlotGeneration;
    internal ulong CapabilityId;
    internal uint TableSlotIndex;
    internal uint SlotIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLocalResourceCapabilityBinding
{
    internal ulong CapabilityId;
    internal ulong Generation;
    internal uint SlotIndex;
    internal uint Reserved0;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourceSpec
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal NativeLocalResourceId ResourceUid;
    internal ulong SizeBytes;
    internal uint RecoveryCostCoefficient;
    internal byte Recoverability;
    internal byte AccessLossImpact;
    internal fixed byte Reserved0[10];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLocalResourceHandle
{
    internal ulong ManagerInstanceId;
    internal ulong TableSlotGeneration;
    internal ulong ResourceSlotGeneration;
    internal NativeLocalResourceId TableId;
    internal ulong TableIncarnation;
    internal NativeLocalResourceId ResourceUid;
    internal uint TableSlotIndex;
    internal uint ResourceSlotIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLocalResourcePartitionHandle
{
    internal ulong ManagerInstanceId;
    internal ulong TableSlotGeneration;
    internal ulong PartitionSlotGeneration;
    internal NativeLocalResourceId TableId;
    internal ulong TableIncarnation;
    internal uint TableSlotIndex;
    internal uint PartitionSlotIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourcePartitionCreateInput
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal NativeLocalResourceTableHandle Table;
    internal NativeLocalResourcePartitionHandle Parent;
    internal uint Capacity;
    internal byte HasParent;
    internal fixed byte Reserved0[3];
    internal ulong Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLocalResourceUseLease
{
    internal NativeLocalResourceHandle Resource;
    internal ulong LeaseGeneration;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourceOperationToken
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong ManagerInstanceId;
    internal ulong OperationId;
    internal ulong OperationGeneration;
    internal ulong StartSnapshotGeneration;
    internal byte Kind;
    internal fixed byte Reserved0[7];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourceOperationView
{
    internal NativeLocalResourceOperationToken Token;
    internal ulong CurrentSnapshotGeneration;
    internal uint SettledExecutionCount;
    internal uint ConfirmedEffectCount;
    internal uint PendingCount;
    internal uint ReservedExecutionCount;
    internal uint StartedExecutionCount;
    internal uint UncertainExecutionCount;
    internal byte Phase;
    internal fixed byte Reserved0[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourceCapacityPlanInput
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal byte Strategy;
    internal fixed byte Reserved0[7];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourceModePlanInput
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal NativeLocalResourceTableHandle Table;
    internal uint MaximumIntents;
    internal byte Mode;
    internal fixed byte Reserved0[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourceIntent
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal NativeLocalResourceHandle Resource;
    internal ulong OperationId;
    internal ulong OperationGeneration;
    internal ulong SnapshotGeneration;
    internal ulong TableRevision;
    internal ulong RowRevision;
    internal ulong ActivityRevision;
    internal ulong UseGeneration;
    internal ulong CapabilityId;
    internal ulong CapabilityGeneration;
    internal ulong SizeBytes;
    internal ulong SettledActivity;
    internal uint RecoveryCostCoefficient;
    internal uint ActionCode;
    internal uint CapabilitySlotIndex;
    internal byte ReasonFlags;
    internal byte ExpectedEffects;
    internal byte Destructive;
    internal byte CapacityPhase;
    internal byte AccessLossImpact;
    internal fixed byte Reserved0[3];
    internal ulong ReclaimPartitionSlotGeneration;
    internal uint ReclaimPartitionSlotIndex;
    internal uint Reserved1;
}

internal enum NativeLocalResourcePlanKind : byte
{
    Capacity = 1,
    Mode = 2,
    Merged = 3,
    Partition = 4,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourcePlanStamp
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong ManagerInstanceId;
    internal ulong OperationId;
    internal ulong OperationGeneration;
    internal ulong SnapshotGeneration;
    internal NativeLocalResourcePlanKind Kind;
    internal fixed byte Reserved0[7];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourcePlanSummary
{
    internal ulong OperationId;
    internal ulong OperationGeneration;
    internal ulong SnapshotGeneration;
    internal uint IntentCount;
    internal uint AffectedTableCount;
    internal uint TriggeredTableCount;
    internal uint StalledTableCount;
    internal uint EmergencyTableCount;
    internal uint Reserved0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLocalResourceExecutionToken
{
    internal NativeLocalResourceHandle Resource;
    internal ulong OperationId;
    internal ulong OperationGeneration;
    internal ulong PendingSlotGeneration;
    internal ulong RowRevision;
    internal ulong ActivityRevision;
    internal ulong UseGeneration;
    internal ulong CapabilityGeneration;
    internal ulong AttemptId;
    internal uint PendingSlotIndex;
    internal uint Reserved0;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourceTypedEffect
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong SizeBytesAfter;
    internal ulong ReleasedBytes;
    internal byte Outcome;
    internal byte Changes;
    internal fixed byte Reserved0[6];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourceCommitReceipt
{
    internal ulong OperationId;
    internal ulong OperationGeneration;
    internal ulong SnapshotGeneration;
    internal ulong TableRevision;
    internal byte ResourceSlotReleased;
    internal byte EffectUncertain;
    internal fixed byte Reserved0[6];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLocalResourceTableCapacityView
{
    internal NativeLocalResourceTableHandle Table;
    internal uint Capacity;
    internal uint OccupiedCount;
    internal uint FreeCount;
    internal uint ActivePendingCount;
    internal uint DirectCapacity;
    internal uint DirectOccupiedCount;
    internal uint DirectFreeCount;
    internal uint PartitionReservationStart;
    internal uint PartitionReservationCapacity;
    internal uint PartitionOccupiedCount;
    internal uint PartitionFreeCount;
    internal uint Reserved0;
    internal ulong Revision;
}

[Flags]
internal enum NativeLocalResourceCapacityTableFlags : uint
{
    Triggered = 1U << 0,
    Affected = 1U << 1,
    Stalled = 1U << 2,
    Emergency = 1U << 3,
    Known = Triggered | Affected | Stalled | Emergency
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLocalResourceCapacityTablePlanView
{
    internal NativeLocalResourceTableCapacityView Capacity;
    internal NativeLocalResourceCapacityTableFlags Flags;
    internal uint Reserved0;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourceView
{
    internal NativeLocalResourceHandle Resource;
    internal ulong SizeBytes;
    internal ulong SettledActivity;
    internal ulong RowRevision;
    internal ulong ActivityRevision;
    internal uint RecoveryCostCoefficient;
    internal byte Recoverability;
    internal byte AccessLossImpact;
    internal byte ProtectedUse;
    internal byte Pending;
    internal fixed byte Reserved0[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourcePartitionAdmissionTicket
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal NativeLocalResourceTableHandle Table;
    internal NativeLocalResourcePartitionHandle Parent;
    internal ulong ManagerInstanceId;
    internal ulong OperationId;
    internal ulong OperationGeneration;
    internal ulong AdmissionId;
    internal ulong ParentStructureRevision;
    internal ulong VictimHash;
    internal uint LocalStart;
    internal uint Capacity;
    internal uint VictimCount;
    internal byte HasParent;
    internal byte Kind;
    internal fixed byte Reserved0[2];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLocalResourcePartitionAdmissionScratch
{
    internal ulong SettledActivity;
    internal uint ResourceCount;
    internal uint LocalStart;
    internal uint SelectionRank;
    internal uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLocalResourcePartitionAdmissionSummary
{
    internal ulong OperationId;
    internal ulong OperationGeneration;
    internal ulong SnapshotGeneration;
    internal uint IntentCount;
    internal uint VictimCount;
    internal uint LocalStart;
    internal uint Capacity;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourcePartitionView
{
    internal NativeLocalResourcePartitionHandle Partition;
    internal NativeLocalResourcePartitionHandle Parent;
    internal ulong SettledActivity;
    internal ulong StructureRevision;
    internal uint LocalStart;
    internal uint Capacity;
    internal uint DirectChildCount;
    internal uint DescendantResourceCount;
    internal byte HasParent;
    internal byte ReclaimProtected;
    internal fixed byte Reserved0[6];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourcePartitionCellView
{
    internal NativeLocalResourcePartitionHandle Partition;
    internal NativeLocalResourceHandle Resource;
    internal NativeLocalResourcePartitionHandle ChildPartition;
    internal uint LocalOrdinal;
    internal byte Kind;
    internal fixed byte Reserved0[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeLocalResourcePartitionResourceAdmission
{
    internal NativeLocalResourcePartitionHandle Partition;
    internal NativeLocalResourceIntent Intent;
    internal NativeLocalResourceSpec Resource;
    internal ulong OperationId;
    internal ulong OperationGeneration;
    internal ulong SnapshotGeneration;
    internal ulong StructureRevision;
    internal uint LocalOrdinal;
    internal byte RequiresRelease;
    internal fixed byte Reserved0[3];
}

internal static unsafe class NativeLocalResourceManagerInterop
{
    private const string LibraryName = NativeAdapterLibraryResolver.LibraryName;
    internal const uint AbiVersion = 10;

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_abi_version();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ulong rm_local_resource_manager_layout_fingerprint();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_config_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_table_spec_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_table_handle_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_capability_spec_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_capability_handle_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_resource_spec_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_resource_handle_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_partition_handle_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_partition_create_input_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_partition_admission_ticket_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_partition_admission_summary_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_partition_admission_scratch_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_partition_view_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_partition_cell_view_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_partition_resource_admission_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_use_lease_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_operation_token_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_operation_view_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_capacity_plan_input_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_mode_plan_input_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_intent_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_plan_stamp_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_execution_token_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_typed_effect_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_plan_summary_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_commit_receipt_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_table_capacity_view_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_capacity_table_plan_view_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_resource_view_size();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_local_resource_manager_state_alignment();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_state_required_size(
        NativeLocalResourceManagerConfig* config,
        ulong* outputSize);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_state_initialize(
        void* state,
        ulong stateSize,
        NativeLocalResourceManagerConfig* config,
        ulong managerInstanceId);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_begin_operation(
        void* state, ulong stateSize, byte kind, NativeLocalResourceOperationToken* token);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_read_operation(
        void* state, ulong stateSize, NativeLocalResourceOperationView* view);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_finish_operation(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* token);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_register_table(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceTableSpec* spec, NativeLocalResourceTableHandle* handle);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_unregister_table(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceTableHandle* handle);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_register_capability(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceTableHandle* table,
        NativeLocalResourceCapabilitySpec* spec, NativeLocalResourceCapabilityHandle* handle);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_replace_capability(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceCapabilityHandle* handle,
        NativeLocalResourceCapabilitySpec* spec, NativeLocalResourceCapabilityHandle* replacement);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_unregister_capability(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceCapabilityHandle* handle);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_register_resource(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceTableHandle* table,
        NativeLocalResourceSpec* spec, NativeLocalResourceHandle* handle);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_register_partition_resource(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourcePartitionHandle* partition,
        uint localOrdinal, NativeLocalResourceSpec* spec, NativeLocalResourceHandle* handle);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_update_resource(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceHandle* handle, NativeLocalResourceSpec* spec);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_unregister_resource(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceHandle* handle);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_touch_resource(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceHandle* handle, uint weight);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_advance_activity_epoch(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        ulong epochCount);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_begin_use(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceHandle* handle, NativeLocalResourceUseLease* lease);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_end_use(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceUseLease* lease);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_plan_capacity(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceCapacityPlanInput* input,
        NativeLocalResourceIntent* scratch, uint scratchCapacity,
        NativeLocalResourceIntent* output, uint outputCapacity, NativeLocalResourcePlanSummary* summary);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_plan_capacity_detailed(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceCapacityPlanInput* input,
        NativeLocalResourceIntent* scratch, uint scratchCapacity,
        NativeLocalResourceIntent* output, uint outputCapacity,
        NativeLocalResourceCapacityTablePlanView* tableOutput, uint tableOutputCapacity,
        NativeLocalResourcePlanSummary* summary);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_plan_mode(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceModePlanInput* input,
        NativeLocalResourceIntent* output, uint outputCapacity, NativeLocalResourcePlanSummary* summary);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_merge_plans_v8(
        NativeLocalResourcePlanStamp* capacityStamp,
        NativeLocalResourcePlanStamp* modeStamp,
        NativeLocalResourceIntent* capacity, uint capacityCount,
        NativeLocalResourceIntent* mode, uint modeCount,
        NativeLocalResourceIntent* output, uint outputCapacity, NativeLocalResourcePlanSummary* summary);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_plan_partition_admission(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourcePartitionCreateInput* input,
        NativeLocalResourcePartitionHandle* victimOutput, uint victimOutputCapacity,
        NativeLocalResourceIntent* intentOutput, uint intentOutputCapacity,
        NativeLocalResourcePartitionAdmissionScratch* scratch, uint scratchCapacity,
        NativeLocalResourcePartitionAdmissionSummary* summary,
        NativeLocalResourcePartitionAdmissionTicket* ticket);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_commit_partition_admission(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourcePartitionAdmissionTicket* ticket,
        NativeLocalResourcePartitionHandle* victims, uint victimCount,
        NativeLocalResourcePartitionHandle* handle);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_plan_partition_close(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourcePartitionHandle* partition,
        NativeLocalResourceIntent* intentOutput, uint intentOutputCapacity,
        NativeLocalResourcePartitionAdmissionSummary* summary,
        NativeLocalResourcePartitionAdmissionTicket* ticket);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_commit_partition_close(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourcePartitionAdmissionTicket* ticket,
        NativeLocalResourcePartitionHandle* partition);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_plan_partition_resource_admission(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourcePartitionHandle* partition,
        NativeLocalResourceSpec* spec,
        NativeLocalResourcePartitionResourceAdmission* output);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_commit_partition_resource_admission(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourcePartitionResourceAdmission* admission,
        NativeLocalResourceHandle* handle);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_read_partition(
        void* state, ulong stateSize, NativeLocalResourcePartitionHandle* partition,
        NativeLocalResourcePartitionView* output);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_read_partition_cell(
        void* state, ulong stateSize, NativeLocalResourcePartitionHandle* partition,
        uint localOrdinal, NativeLocalResourcePartitionCellView* output);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_begin_intent(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceIntent* intent, NativeLocalResourceExecutionToken* token);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_commit_effect(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceExecutionToken* token,
        NativeLocalResourceTypedEffect* effect, NativeLocalResourceCommitReceipt* receipt);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_mark_effect_started(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceExecutionToken* token, NativeLocalResourceCommitReceipt* receipt);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_abort_intent(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceExecutionToken* token,
        NativeLocalResourceCommitReceipt* receipt);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_mark_effect_uncertain(
        void* state, ulong stateSize, NativeLocalResourceOperationToken* operation,
        NativeLocalResourceExecutionToken* token,
        NativeLocalResourceCommitReceipt* receipt);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_read_table_capacity(
        void* state, ulong stateSize, NativeLocalResourceTableHandle* handle,
        NativeLocalResourceTableCapacityView* output);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int rm_local_resource_manager_read_resource(
        void* state, ulong stateSize, NativeLocalResourceHandle* handle, NativeLocalResourceView* output);
}
