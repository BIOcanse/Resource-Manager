using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.Adapter.NativeScheduling;

internal enum NativeResourceSchedulerResultCode : int
{
    Ok = 0,
    InvalidArgument = 1,
    InvalidConfig = 2,
    InvalidRequest = 3,
    InvalidTarget = 4,
    InvalidCapacity = 5,
    InvalidPending = 6,
    BufferTooSmall = 7,
    NumericOverflow = 8,
    InvalidState = 9,
    StateFull = 10,
    StaleReservation = 11,
    InvalidFeedback = 12
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeResourceSchedulerConfig
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong Generation;
    internal ulong BytesPerMegabyte;
    internal double SizeImportanceMinimum;
    internal double SizeImportanceMaximum;
    internal double SizeLogDivisor;
    internal fixed double ResourceKindMultiplier[ResourceSchedulerConfig.ResourceKindCount];
    internal fixed double SurfaceMultiplier[ResourceSchedulerConfig.SurfaceCount];
    internal fixed double PolicyGradeMultiplier[ResourceSchedulerConfig.PolicyGradeCount];
    internal fixed byte PolicyGradePressure[ResourceSchedulerConfig.PolicyGradeCount];
    internal fixed byte Reserved0[3];
    internal fixed double SchedulingGradeMultiplier[ResourceSchedulerConfig.SchedulingGradeCount];
    internal double AbsentSchedulingGradeMultiplier;
    internal fixed double ActionKindMultiplier[ResourceSchedulerConfig.SchedulingGradeCount * ResourceSchedulerConfig.ResourceKindCount];
    internal double ActivityBaseMultiplier;
    internal double ActivityQuadraticScale;
    internal double ActivityNormalizer;
    internal fixed double DemandMultiplier[ResourceSchedulerConfig.DemandMultiplierCount];
    internal ulong LargeResourceBytes;
    internal byte HighActivityScore;
    internal byte PhysicalToVirtualDesperatePressureLevel;
    internal fixed byte Reserved1[6];
    internal double PhysicalToVirtualMinimumFreeRatio;
    internal double PhysicalToVirtualDesperateMinimumFreeRatio;
    internal double VramToPhysicalMinimumFreeRatio;
    internal fixed double PressureFreeRatioThreshold[ResourceSchedulerConfig.PressureThresholdCount];
    internal fixed double TargetFreeRatio[ResourceSchedulerConfig.TierCount];
    internal fixed double DesiredFreeRatioLevel2To4[ResourceSchedulerConfig.TierCount];
    internal fixed ulong MinimumReleaseBytes[ResourceSchedulerConfig.PressureLevelCount];
    internal fixed double MaximumReleaseShare[ResourceSchedulerConfig.PressureLevelCount];
    internal fixed double BaseScoreThreshold[ResourceSchedulerConfig.PressureThresholdCount];
    internal fixed double BaseReleaseMultiplier[ResourceSchedulerConfig.PressureLevelCount];
    internal double BaseScoreMinimum;
    internal double BaseScoreMaximum;
    internal uint TrimReleaseNumerator;
    internal uint TrimReleaseDenominator;
    internal uint MaximumActionsPerTarget;
    internal uint Reserved2;
    internal double StrongPressureFreeRatio;
    internal double DangerMinimumPhysicalAfterVramMoveRatio;
    internal double DangerMinimumVirtualAfterPhysicalMoveRatio;
    internal fixed byte DangerSeverityWeight[ResourceSchedulerConfig.DangerFlagCount];
    internal fixed byte Reserved3[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeResourceSchedulerCapacity
{
    internal ulong TotalVramBytes;
    internal ulong FreeVramBytes;
    internal ulong TotalPhysicalBytes;
    internal ulong FreePhysicalBytes;
    internal ulong TotalVirtualBytes;
    internal ulong FreeVirtualBytes;
    internal double FallbackVramFreeRatio;
    internal double FallbackPhysicalFreeRatio;
    internal double FallbackVirtualFreeRatio;
    internal byte FreeBytesValidMask;
    internal fixed byte Reserved0[7];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeResourceSchedulerPlanRequest
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong RequestId;
    internal ulong ConfigurationGeneration;
    internal ulong NowMonotonicTimestamp;
    internal byte RequestedActionMask;
    internal byte EnabledTierMask;
    internal byte Flags;
    internal byte Reserved0;
    internal uint Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeResourceSchedulerTarget
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong TargetKey;
    internal ulong OwnerApplicationKey;
    internal ulong OwnerInstanceIdLow;
    internal ulong OwnerInstanceIdHigh;
    internal ulong OwnerContextGeneration;
    internal ulong LeaseGeneration;
    internal ulong CapabilityGeneration;
    internal double BaseScore;
    internal NativeResourceSchedulerCapacity Capacity;
    internal uint PrivateResourceStart;
    internal uint PrivateResourceCount;
    internal sbyte PolicyGrade;
    internal byte CpuGrade;
    internal byte GpuGrade;
    internal byte SurfaceState;
    internal byte TargetFlags;
    internal fixed byte Reserved0[3];
    internal uint Reserved1;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 152)]
internal struct NativeResourceSchedulerExecutionAuthority
{
    internal byte Source;
    internal byte Action;
    internal byte ActionRoute;
    internal byte Flags;
    internal uint ResourceSlot;
    internal uint ResourceId;
    internal uint Reserved0;
    internal ulong LedgerInstanceId;
    internal ulong SourceSnapshotGeneration;
    internal ulong ResourceGeneration;
    internal ulong ResourceKey;
    internal ulong OwnerApplicationKey;
    internal ulong OwnerInstanceIdLow;
    internal ulong OwnerInstanceIdHigh;
    internal ulong OwnerContextGeneration;
    internal ulong LeaseGeneration;
    internal ulong BindingGeneration;
    internal ulong CapabilityGeneration;
    internal ulong SchedulingRevision;
    internal ulong ExecutorIdLow;
    internal ulong ExecutorIdHigh;
    internal ulong ActionAttemptIdLow;
    internal ulong ActionAttemptIdHigh;
    internal ulong ProjectionEpoch;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 496)]
internal unsafe struct NativeResourceSchedulerPrivateResource
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong TargetKey;
    internal ulong SizeBytes;
    internal NativeResourceSchedulerExecutionAuthority DiscardAuthority;
    internal NativeResourceSchedulerExecutionAuthority TrimAuthority;
    internal NativeResourceSchedulerExecutionAuthority MoveDownAuthority;
    internal byte Tier;
    internal byte ResourceKind;
    internal byte RecoveryKind;
    internal byte Granularity;
    internal byte InapplicableActions;
    internal byte ActionRoute;
    internal byte DemandMask;
    internal byte ActivityScore;
    internal byte Reserved0;
    internal fixed byte Reserved1[4];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 216)]
internal unsafe struct NativeResourceSchedulerPendingAction
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal NativeResourceSchedulerExecutionAuthority Authority;
    internal ulong JournalTransactionIdLow;
    internal ulong JournalTransactionIdHigh;
    internal ulong TargetKey;
    internal ulong SizeBytes;
    internal ulong DeadlineTimestamp;
    internal ulong PendingGeneration;
    internal byte State;
    internal byte Tier;
    internal byte Flags;
    internal fixed byte Reserved0[5];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeResourceSchedulerReservationRequest
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong NowMonotonicTimestamp;
    internal ulong DeadlineTimestamp;
    internal ulong PendingGeneration;
    internal ulong ConfigurationGeneration;
    internal double DangerMinimumPhysicalAfterVramMoveRatio;
    internal double DangerMinimumVirtualAfterPhysicalMoveRatio;
    internal uint MaximumInFlight;
    internal uint MaximumInFlightPerTarget;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeResourceSchedulerReservationToken
{
    internal ulong StateGeneration;
    internal ulong SlotGeneration;
    internal ulong PendingGeneration;
    internal uint SlotIndex;
    internal uint Reserved0;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 192)]
internal unsafe struct NativeResourceSchedulerRevalidationInput
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal NativeResourceSchedulerExecutionAuthority Authority;
    internal ulong TargetKey;
    internal ulong SizeBytes;
    internal sbyte PolicyGrade;
    internal byte Tier;
    internal byte Flags;
    internal byte ResourceKind;
    internal byte RecoveryKind;
    internal byte Granularity;
    internal byte InapplicableActions;
    internal byte DemandMask;
    internal byte ActivityScore;
    internal byte Reserved0;
    internal fixed byte Reserved1[6];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeResourceSchedulerRevalidationOutput
{
    internal byte Allowed;
    internal byte Reason;
    internal fixed byte Reserved0[6];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeResourceSchedulerFeedbackRequest
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong ExpectedRequestId;
    internal byte PolicyResultCode;
    internal byte PolicyDangerFlags;
    internal byte PolicyAccepted;
    internal byte ActionResultCount;
    internal uint Reserved0;
    internal ulong Reserved1;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 192)]
internal unsafe struct NativeResourceSchedulerActionFeedback
{
    internal NativeResourceSchedulerExecutionAuthority Authority;
    internal ulong RequestId;
    internal ulong ReleasedBytes;
    internal ulong ResidentBytes;
    internal ulong ActionGateEpoch;
    internal ushort DetailCode;
    internal byte Status;
    internal byte PreviousTier;
    internal byte CurrentTier;
    internal fixed byte Reserved0[3];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeResourceSchedulerFeedbackOutput
{
    internal NativeResourceSchedulerCapacity ProjectedCapacity;
    internal byte Applied;
    internal byte ResultCode;
    internal byte DangerFlags;
    internal byte CurrentTier;
    internal ushort ErrorCode;
    internal byte ReservationDisposition;
    internal byte Reserved0;
    internal ulong Reserved1;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 216)]
internal struct NativeResourceSchedulerCandidate
{
    internal ulong TargetKey;
    internal NativeResourceSchedulerExecutionAuthority Authority;
    internal ulong SizeBytes;
    internal ulong EstimatedReleaseBytes;
    internal double OwnerImportance;
    internal double FinalImportance;
    internal uint ResourceInputIndex;
    internal uint TargetInputIndex;
    internal uint Rank;
    internal uint ResourceId;
    internal byte Tier;
    internal byte ResourceKind;
    internal byte ActivityScore;
    internal byte SurfaceState;
    internal byte PhaseOrder;
    internal byte Flags;
    internal ushort Reserved0;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 216)]
internal unsafe struct NativeResourceSchedulerSelection
{
    internal ulong TargetKey;
    internal ulong RequestId;
    internal NativeResourceSchedulerExecutionAuthority Authority;
    internal ulong SizeBytes;
    internal ulong EstimatedReleaseBytes;
    internal ulong ConfigurationGeneration;
    internal double FinalImportance;
    internal uint CandidateIndex;
    internal uint TargetInputIndex;
    internal byte Tier;
    internal byte ActivityScore;
    internal fixed byte Reserved0[6];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeResourceSchedulerTargetPlan
{
    internal ulong TargetKey;
    internal ulong OwnerApplicationKey;
    internal fixed ulong ReleaseGoalBytes[ResourceSchedulerConfig.TierCount];
    internal uint CandidateCount;
    internal uint SelectedActionCount;
    internal uint FirstSelectionIndex;
    internal uint ActionLimit;
    internal fixed byte PressureLevel[ResourceSchedulerConfig.TierCount];
    internal byte ActivePhaseOrder;
    internal byte ResultCode;
    internal byte DangerFlags;
    internal byte TargetFlags;
    internal fixed byte Reserved0[7];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeResourceSchedulerGlobalSelection
{
    internal ulong TargetKey;
    internal ulong OwnerApplicationKey;
    internal uint CandidateIndex;
    internal uint SelectionIndex;
    internal uint TargetInputIndex;
    internal uint DangerSeverity;
    internal byte Kind;
    internal byte ResultCode;
    internal byte DangerFlags;
    internal byte Reserved0;
    internal uint Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeResourceSchedulerPlanSummary
{
    internal ulong RequestId;
    internal ulong ConfigurationGeneration;
    internal uint TargetCount;
    internal uint ResourceCount;
    internal uint ActivePendingCount;
    internal uint PendingDuplicateCount;
    internal uint CandidateCapacityRequired;
    internal uint RawCandidateCount;
    internal uint CandidateCount;
    internal uint SelectionCount;
    internal uint InvalidResourceCount;
    internal uint RequiredNowCount;
    internal uint NoLegalActionCount;
    internal uint DemandBlockedCount;
    internal uint IntrinsicBlockedCount;
    internal uint CapacityBlockedCount;
    internal uint InvalidNumericCount;
    internal uint PendingBlockedCount;
    internal uint DangerTargetCount;
    internal uint Reserved0A;
    internal uint Reserved0B;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 16)]
internal unsafe struct NativeResourceSchedulerResourceScratch
{
    internal uint TargetInputIndex;
    internal uint GroupIndex;
    internal byte Flags;
    internal fixed byte Reserved0[7];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeResourceSchedulerTargetScratch
{
    internal fixed ulong LedgerBytes[ResourceSchedulerConfig.TierCount];
    internal ulong ReservedPhysicalBytes;
    internal ulong ReservedVirtualBytes;
    internal uint RawCandidateCount;
    internal uint CandidateCount;
    internal uint SelectedActionCount;
    internal uint ActionLimit;
    internal byte ActivePhaseOrder;
    internal byte ResultCode;
    internal byte DangerFlags;
    internal byte Reserved0;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 56)]
internal unsafe struct NativeResourceSchedulerFactBatchInput
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong ProjectionEpoch;
    internal ulong ConfigurationGeneration;
    internal uint TargetCount;
    internal uint PrivateResourceCount;
    internal uint JournalPendingCount;
    internal uint ActionBudget;
    internal uint Flags;
    internal uint Reserved0;
    internal ulong Reserved1;
}

internal static unsafe class NativeResourceSchedulerInterop
{
    internal const string LibraryName = "ResourceManager.Adapter.Native";
    internal const uint AbiVersion = ResourceSchedulerProtocol.Version;

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_abi_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_config_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_plan_request_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_capacity_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_target_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_private_resource_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_pending_action_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_candidate_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_selection_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_resource_scratch_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_execution_authority_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_fact_batch_input_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_reservation_request_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_reservation_token_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_revalidation_input_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_revalidation_output_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_feedback_request_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_action_feedback_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_feedback_output_size();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ulong rm_resource_scheduler_layout_fingerprint();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint rm_resource_scheduler_state_alignment();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResourceSchedulerResultCode rm_resource_scheduler_state_required_size(
        uint capacity,
        out ulong outputSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResourceSchedulerResultCode rm_resource_scheduler_state_initialize(
        void* stateBuffer,
        ulong stateSize,
        uint capacity,
        ulong generation);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResourceSchedulerResultCode rm_resource_scheduler_reserve_selection(
        void* stateBuffer,
        ulong stateSize,
        NativeResourceSchedulerReservationRequest* request,
        NativeResourceSchedulerSelection* selection,
        NativeResourceSchedulerReservationToken* token);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResourceSchedulerResultCode rm_resource_scheduler_revalidate_and_begin_reservation(
        void* stateBuffer,
        ulong stateSize,
        NativeResourceSchedulerConfig* config,
        NativeResourceSchedulerReservationToken* token,
        NativeResourceSchedulerSelection* selection,
        NativeResourceSchedulerRevalidationInput* input,
        NativeResourceSchedulerRevalidationOutput* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResourceSchedulerResultCode rm_resource_scheduler_complete_reservation(
        void* stateBuffer,
        ulong stateSize,
        NativeResourceSchedulerReservationToken* token);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResourceSchedulerResultCode rm_resource_scheduler_cancel_reservation(
        void* stateBuffer,
        ulong stateSize,
        NativeResourceSchedulerReservationToken* token);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResourceSchedulerResultCode rm_resource_scheduler_bind_reservation_journal(
        void* stateBuffer,
        ulong stateSize,
        NativeResourceSchedulerReservationToken* token,
        ulong transactionIdLow,
        ulong transactionIdHigh);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResourceSchedulerResultCode rm_resource_scheduler_abandon_reservation(
        void* stateBuffer,
        ulong stateSize,
        NativeResourceSchedulerReservationToken* token);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResourceSchedulerResultCode rm_resource_scheduler_apply_feedback(
        void* stateBuffer,
        ulong stateSize,
        NativeResourceSchedulerReservationToken* token,
        NativeResourceSchedulerFeedbackRequest* request,
        NativeResourceSchedulerSelection* selection,
        NativeResourceSchedulerCapacity* capacity,
        NativeResourceSchedulerActionFeedback* action,
        NativeResourceSchedulerFeedbackOutput* output);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResourceSchedulerResultCode rm_resource_scheduler_validate_config(
        NativeResourceSchedulerConfig* config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResourceSchedulerResultCode rm_resource_scheduler_required_candidate_capacity(
        uint privateResourceCount,
        out uint outputCapacity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResourceSchedulerResultCode rm_resource_scheduler_plan(
        NativeResourceSchedulerConfig* config,
        NativeResourceSchedulerPlanRequest* request,
        NativeResourceSchedulerTarget* targets,
        uint targetCount,
        NativeResourceSchedulerPrivateResource* resources,
        uint resourceCount,
        void* stateBuffer,
        ulong stateSize,
        NativeResourceSchedulerPendingAction* pendingInput,
        uint pendingInputCapacity,
        NativeResourceSchedulerPendingAction* journalPending,
        uint journalPendingCount,
        NativeResourceSchedulerFactBatchInput* factBatch,
        NativeResourceSchedulerCandidate* candidates,
        uint candidateCapacity,
        NativeResourceSchedulerSelection* selections,
        uint selectionCapacity,
        NativeResourceSchedulerTargetPlan* targetPlans,
        uint targetPlanCapacity,
        NativeResourceSchedulerResourceScratch* resourceScratch,
        uint resourceScratchCapacity,
        NativeResourceSchedulerTargetScratch* targetScratch,
        uint targetScratchCapacity,
        uint* resourceOrder,
        uint resourceOrderCapacity,
        NativeResourceSchedulerPendingAction* pendingScratch,
        uint pendingScratchCapacity,
        NativeResourceSchedulerGlobalSelection* global,
        NativeResourceSchedulerPlanSummary* summary);

    internal static uint SizeOfConfig() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerConfig>());

    internal static uint SizeOfPlanRequest() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerPlanRequest>());

    internal static uint SizeOfCapacity() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerCapacity>());

    internal static uint SizeOfTarget() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerTarget>());

    internal static uint SizeOfPrivateResource() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerPrivateResource>());

    internal static uint SizeOfPendingAction() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerPendingAction>());

    internal static uint SizeOfCandidate() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerCandidate>());

    internal static uint SizeOfSelection() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerSelection>());

    internal static uint SizeOfResourceScratch() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerResourceScratch>());

    internal static uint SizeOfExecutionAuthority() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerExecutionAuthority>());

    internal static uint SizeOfFactBatchInput() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerFactBatchInput>());

    internal static uint SizeOfReservationRequest() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerReservationRequest>());

    internal static uint SizeOfReservationToken() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerReservationToken>());

    internal static uint SizeOfRevalidationInput() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerRevalidationInput>());

    internal static uint SizeOfRevalidationOutput() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerRevalidationOutput>());

    internal static uint SizeOfFeedbackRequest() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerFeedbackRequest>());

    internal static uint SizeOfActionFeedback() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerActionFeedback>());

    internal static uint SizeOfFeedbackOutput() => checked((uint)Unsafe.SizeOf<NativeResourceSchedulerFeedbackOutput>());
}
