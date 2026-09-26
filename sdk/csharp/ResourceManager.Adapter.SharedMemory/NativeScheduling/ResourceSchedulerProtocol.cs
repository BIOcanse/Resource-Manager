using ResourceManager.Adapter;
using System.Collections.Immutable;

namespace ResourceManager.Adapter.NativeScheduling;

public static class ResourceSchedulerProtocol
{
    public const uint Version = 11;
}

public enum ResourceSchedulerRevalidationReason : byte
{
    Allowed = 0,
    SnapshotUnavailable = 1,
    SnapshotOlder = 2,
    IdentityChanged = 3,
    ResourceChanged = 4,
    RequiredNow = 5,
    ActionBlocked = 6
}

public enum ResourceSchedulerSchedulingGrade : byte
{
    Freeze = 0,
    Optimize = 1,
    Normal = 2,
    Extreme = 3,
    Unspecified = byte.MaxValue
}

public enum ResourceSchedulerSource : byte
{
    AdaptedPrivate = 0
}

public readonly record struct ResourceSchedulerExecutionAuthority(
    ResourceSchedulerSource Source,
    AdapterResourceActionMask Action,
    AdapterResourceActionRoute ActionRoute,
    byte Flags,
    uint ResourceSlot,
    uint ResourceId,
    ulong LedgerInstanceId,
    ulong SourceSnapshotGeneration,
    ulong ResourceGeneration,
    ulong ResourceKey,
    ulong OwnerApplicationKey,
    ulong OwnerInstanceIdLow,
    ulong OwnerInstanceIdHigh,
    ulong OwnerContextGeneration,
    ulong LeaseGeneration,
    ulong BindingGeneration,
    ulong CapabilityGeneration,
    ulong SchedulingRevision,
    ulong ExecutorIdLow,
    ulong ExecutorIdHigh,
    ulong ActionAttemptIdLow,
    ulong ActionAttemptIdHigh,
    ulong ProjectionEpoch)
{
    public const byte TypedExecutorProof = 1 << 0;
    public const byte HostSelfExecutor = 1 << 1;
    public const byte HostSelfExecutorProof =
        TypedExecutorProof | HostSelfExecutor;
}

public enum ResourceSchedulerSelectionKind : byte
{
    None = 0,
    Action = 1,
    Danger = 2
}

public sealed record ResourceSchedulerConfig(
    ulong Generation,
    ulong BytesPerMegabyte,
    double SizeImportanceMinimum,
    double SizeImportanceMaximum,
    double SizeLogDivisor,
    ImmutableArray<double> ResourceKindMultipliers,
    ImmutableArray<double> SurfaceMultipliers,
    ImmutableArray<double> PolicyGradeMultipliers,
    ImmutableArray<byte> PolicyGradePressure,
    ImmutableArray<double> SchedulingGradeMultipliers,
    double AbsentSchedulingGradeMultiplier,
    ImmutableArray<double> DiscardKindMultipliers,
    ImmutableArray<double> TrimKindMultipliers,
    ImmutableArray<double> MoveDownKindMultipliers,
    ImmutableArray<double> MoveUpKindMultipliers,
    double ActivityBaseMultiplier,
    double ActivityQuadraticScale,
    double ActivityNormalizer,
    ImmutableArray<double> DemandMultipliers,
    ulong LargeResourceBytes,
    byte HighActivityScore,
    byte PhysicalToVirtualDesperatePressureLevel,
    double PhysicalToVirtualMinimumFreeRatio,
    double PhysicalToVirtualDesperateMinimumFreeRatio,
    double VramToPhysicalMinimumFreeRatio,
    ImmutableArray<double> PressureFreeRatioThresholds,
    ImmutableArray<double> TargetFreeRatios,
    ImmutableArray<double> DesiredFreeRatiosLevel2To4,
    ImmutableArray<ulong> MinimumReleaseBytes,
    ImmutableArray<double> MaximumReleaseShares,
    ImmutableArray<double> BaseScoreThresholds,
    ImmutableArray<double> BaseReleaseMultipliers,
    double BaseScoreMinimum,
    double BaseScoreMaximum,
    uint TrimReleaseNumerator,
    uint TrimReleaseDenominator,
    uint MaximumActionsPerTarget,
    double StrongPressureFreeRatio,
    double DangerMinimumPhysicalAfterVramMoveRatio,
    double DangerMinimumVirtualAfterPhysicalMoveRatio,
    ImmutableArray<byte> DangerSeverityWeights)
{
    public const int ResourceKindCount = 13;
    public const int SurfaceCount = 5;
    public const int PolicyGradeCount = 5;
    public const int SchedulingGradeCount = 4;
    public const int DemandMultiplierCount = 3;
    public const int TierCount = 3;
    public const int PressureThresholdCount = 3;
    public const int PressureLevelCount = 4;
    public const int DangerFlagCount = 5;
}

public readonly record struct ResourceSchedulerCapacity(
    ulong TotalVramBytes,
    ulong FreeVramBytes,
    ulong TotalPhysicalBytes,
    ulong FreePhysicalBytes,
    ulong TotalVirtualBytes,
    ulong FreeVirtualBytes,
    double FallbackVramFreeRatio,
    double FallbackPhysicalFreeRatio,
    double FallbackVirtualFreeRatio,
    byte FreeBytesValidMask);

public readonly record struct ResourceSchedulerPlanRequest(
    ulong RequestId,
    ulong ConfigurationGeneration,
    ulong NowMonotonicTimestamp,
    AdapterResourceActionMask RequestedActions,
    byte EnabledTierMask,
    bool EmitCandidates);

public readonly record struct ResourceSchedulerReservationToken(
    ulong StateGeneration,
    ulong SlotGeneration,
    ulong PendingGeneration,
    uint SlotIndex);

public readonly record struct ResourceSchedulerRevalidationResource(
    ResourceSchedulerExecutionAuthority Authority,
    bool SnapshotAvailable,
    ulong TargetKey,
    ulong SizeBytes,
    sbyte PolicyGrade,
    AdapterResourceTier Tier,
    AdapterResourceKind ResourceKind,
    AdapterResourceRecoveryKind RecoveryKind,
    AdapterResourceGranularity Granularity,
    AdapterResourceActionMask InapplicableActions,
    AdapterResourceDemandMask DemandMask,
    byte ActivityScore);

public readonly record struct ResourceSchedulerRevalidationResult(
    bool Allowed,
    ResourceSchedulerRevalidationReason Reason);

public readonly record struct ResourceSchedulerFeedbackRequest(
    ulong ExpectedRequestId,
    byte PolicyResultCode,
    byte PolicyDangerFlags,
    bool PolicyAccepted,
    byte ActionResultCount);

public readonly record struct ResourceSchedulerActionFeedback(
    ResourceSchedulerExecutionAuthority Authority,
    ulong RequestId,
    ulong ReleasedBytes,
    ulong ResidentBytes,
    ulong ActionGateEpoch,
    ushort DetailCode,
    AdapterResourceActionStatus Status,
    AdapterResourceTier PreviousTier,
    AdapterResourceTier CurrentTier);

public enum ResourceSchedulerReservationDisposition : byte
{
    Retained = 1,
    Completed = 2
}

public readonly record struct ResourceSchedulerFeedbackResult(
    ResourceSchedulerCapacity ProjectedCapacity,
    bool Applied,
    ResourceSchedulerReservationDisposition ReservationDisposition,
    byte ResultCode,
    byte DangerFlags,
    AdapterResourceTier CurrentTier,
    ushort ErrorCode);

public readonly record struct ResourceSchedulerTarget(
    ulong TargetKey,
    ulong OwnerApplicationKey,
    ulong OwnerInstanceIdLow,
    ulong OwnerInstanceIdHigh,
    ulong OwnerContextGeneration,
    ulong LeaseGeneration,
    ulong CapabilityGeneration,
    double BaseScore,
    ResourceSchedulerCapacity Capacity,
    uint PrivateResourceStart,
    uint PrivateResourceCount,
    sbyte PolicyGrade,
    ResourceSchedulerSchedulingGrade CpuGrade,
    ResourceSchedulerSchedulingGrade GpuGrade,
    AdapterSoftwareSurfaceState SurfaceState,
    bool IncludeVram,
    bool SoftwareMode);

public readonly record struct ResourceSchedulerPrivateResource(
    ulong TargetKey,
    ulong SizeBytes,
    ResourceSchedulerExecutionAuthority DiscardAuthority,
    ResourceSchedulerExecutionAuthority TrimAuthority,
    ResourceSchedulerExecutionAuthority MoveDownAuthority,
    AdapterResourceTier Tier,
    AdapterResourceKind ResourceKind,
    AdapterResourceRecoveryKind RecoveryKind,
    AdapterResourceGranularity Granularity,
    AdapterResourceActionMask InapplicableActions,
    AdapterResourceActionRoute ActionRoute,
    AdapterResourceDemandMask DemandMask,
    byte ActivityScore);

public readonly record struct ResourceSchedulerCandidate(
    ulong TargetKey,
    ResourceSchedulerExecutionAuthority Authority,
    ulong SizeBytes,
    ulong EstimatedReleaseBytes,
    double OwnerImportance,
    double FinalImportance,
    uint ResourceInputIndex,
    uint TargetInputIndex,
    uint Rank,
    uint ResourceId,
    AdapterResourceTier Tier,
    AdapterResourceKind ResourceKind,
    byte ActivityScore,
    AdapterSoftwareSurfaceState SurfaceState,
    byte PhaseOrder,
    byte Flags);

public readonly record struct ResourceSchedulerSelection(
    ulong TargetKey,
    ulong RequestId,
    ResourceSchedulerExecutionAuthority Authority,
    ulong SizeBytes,
    ulong EstimatedReleaseBytes,
    ulong ConfigurationGeneration,
    double FinalImportance,
    uint CandidateIndex,
    uint TargetInputIndex,
    AdapterResourceTier Tier,
    byte ActivityScore);

public enum ResourceSchedulerPendingState : byte
{
    Queued = 1,
    Reserved = 2,
    Active = 3,
    JournalPending = 4,
    EffectUncertain = 5
}

public readonly record struct ResourceSchedulerPendingAction(
    ResourceSchedulerExecutionAuthority Authority,
    ulong JournalTransactionIdLow,
    ulong JournalTransactionIdHigh,
    ulong TargetKey,
    ulong SizeBytes,
    ulong DeadlineTimestamp,
    ulong PendingGeneration,
    ResourceSchedulerPendingState State,
    AdapterResourceTier Tier);

public sealed record ResourceSchedulerFactBatch(
    ulong ProjectionEpoch,
    uint ActionBudget,
    IReadOnlyList<ResourceSchedulerPendingAction> JournalPending);

public readonly record struct ResourceSchedulerTargetPlan(
    ulong TargetKey,
    ulong OwnerApplicationKey,
    IReadOnlyList<ulong> ReleaseGoalBytes,
    uint CandidateCount,
    uint SelectedActionCount,
    uint FirstSelectionIndex,
    uint ActionLimit,
    IReadOnlyList<byte> PressureLevel,
    byte ActivePhaseOrder,
    byte ResultCode,
    byte DangerFlags,
    byte TargetFlags);

public readonly record struct ResourceSchedulerGlobalSelection(
    ulong TargetKey,
    ulong OwnerApplicationKey,
    uint CandidateIndex,
    uint SelectionIndex,
    uint TargetInputIndex,
    uint DangerSeverity,
    ResourceSchedulerSelectionKind Kind,
    byte ResultCode,
    byte DangerFlags);

public readonly record struct ResourceSchedulerPlanSummary(
    ulong RequestId,
    ulong ConfigurationGeneration,
    uint TargetCount,
    uint ResourceCount,
    uint ActivePendingCount,
    uint PendingDuplicateCount,
    uint CandidateCapacityRequired,
    uint RawCandidateCount,
    uint CandidateCount,
    uint SelectionCount,
    uint InvalidResourceCount,
    uint RequiredNowCount,
    uint NoLegalActionCount,
    uint DemandBlockedCount,
    uint IntrinsicBlockedCount,
    uint CapacityBlockedCount,
    uint InvalidNumericCount,
    uint PendingBlockedCount,
    uint DangerTargetCount);

public sealed record ResourceSchedulerPlanResult(
    ResourceSchedulerPlanSummary Summary,
    ResourceSchedulerGlobalSelection GlobalSelection,
    IReadOnlyList<ResourceSchedulerCandidate> Candidates,
    IReadOnlyList<ResourceSchedulerSelection> Selections,
    IReadOnlyList<ResourceSchedulerTargetPlan> TargetPlans);
