using System.Globalization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

// These literals are the wire contract, independent of C# enum identifier spelling.
internal static class HostManagerDecisionDiagnosticTokens
{
    internal static string Token(HostManagerSchedulingAuthorityAvailability value)
        => value switch
        {
            HostManagerSchedulingAuthorityAvailability.Unavailable => "unavailable",
            HostManagerSchedulingAuthorityAvailability.ComputeOnly => "compute_only",
            HostManagerSchedulingAuthorityAvailability.Ready => "ready",
            _ => ((int)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(HostManagerCpuOccupancySource value)
        => value switch
        {
            HostManagerCpuOccupancySource.CoreResidency => "core_residency",
            HostManagerCpuOccupancySource.ProcessCpuUsage => "process_cpu_usage",
            _ => ((int)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(NativeComputeScoringOutputKind value)
        => value switch
        {
            NativeComputeScoringOutputKind.ProcessCpu => "process_cpu",
            NativeComputeScoringOutputKind.ProcessGpu => "process_gpu",
            NativeComputeScoringOutputKind.SoftwareCpu => "software_cpu",
            NativeComputeScoringOutputKind.SoftwareGpu => "software_gpu",
            NativeComputeScoringOutputKind.SoftwareMemory => "software_memory",
            _ => ((int)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(NativeMemoryMode value)
        => value switch
        {
            NativeMemoryMode.Invalid => "invalid",
            NativeMemoryMode.Unrestricted => "unrestricted",
            NativeMemoryMode.Normal => "normal",
            NativeMemoryMode.Optimize => "optimize",
            NativeMemoryMode.PagedFrozen => "paged_frozen",
            _ => ((int)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(NativeMemoryGradeSet value)
        => value switch
        {
            NativeMemoryGradeSet.None => "none",
            NativeMemoryGradeSet.Normal => "normal",
            NativeMemoryGradeSet.L1 => "l1",
            NativeMemoryGradeSet.L2 => "l2",
            NativeMemoryGradeSet.L3 => "l3",
            NativeMemoryGradeSet.L4 => "l4",
            NativeMemoryGradeSet.Known => "known",
            _ => ((int)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(NativeSmartCoordinatorSnapshotFlags value)
        => value switch
        {
            NativeSmartCoordinatorSnapshotFlags.None => "none",
            NativeSmartCoordinatorSnapshotFlags.HasInvalidFacts => "has_invalid_facts",
            NativeSmartCoordinatorSnapshotFlags.EventBoostActive => "event_boost_active",
            NativeSmartCoordinatorSnapshotFlags.ScoreOnly => "score_only",
            NativeSmartCoordinatorSnapshotFlags.AwaitingFeedback => "awaiting_feedback",
            NativeSmartCoordinatorSnapshotFlags.RequiresAuthoritativeResync => "requires_authoritative_resync",
            NativeSmartCoordinatorSnapshotFlags.Known => "known",
            _ => ((uint)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(NativeSmartCoordinatorReason value)
        => value switch
        {
            NativeSmartCoordinatorReason.None => "none",
            NativeSmartCoordinatorReason.ReservedSystemReason0 => "reserved_system_reason0",
            NativeSmartCoordinatorReason.MissingProcessIdentity => "missing_process_identity",
            NativeSmartCoordinatorReason.MissingBaseScore => "missing_base_score",
            NativeSmartCoordinatorReason.MissingSurfaceFacts => "missing_surface_facts",
            NativeSmartCoordinatorReason.MissingCpuMetric => "missing_cpu_metric",
            NativeSmartCoordinatorReason.ReservedProcessReason5 => "reserved_process_reason5",
            NativeSmartCoordinatorReason.IncompleteGpuMetrics => "incomplete_gpu_metrics",
            NativeSmartCoordinatorReason.Ineligible => "ineligible",
            NativeSmartCoordinatorReason.NotRunning => "not_running",
            NativeSmartCoordinatorReason.HighTierBlocksOptimization => "high_tier_blocks_optimization",
            NativeSmartCoordinatorReason.MiddleTierBlocksEnhancement => "middle_tier_blocks_enhancement",
            NativeSmartCoordinatorReason.MiddleTierBlocksFreeze => "middle_tier_blocks_freeze",
            NativeSmartCoordinatorReason.LowTierBlocksEnhancement => "low_tier_blocks_enhancement",
            NativeSmartCoordinatorReason.ProtectionLevel1ClampsFreeze => "protection_level1_clamps_freeze",
            NativeSmartCoordinatorReason.ProtectionLevel2BlocksOptimization => "protection_level2_blocks_optimization",
            NativeSmartCoordinatorReason.ReservedProcessReason15 => "reserved_process_reason15",
            NativeSmartCoordinatorReason.ReservedProcessReason16 => "reserved_process_reason16",
            NativeSmartCoordinatorReason.FreezeGroupBlocked => "freeze_group_blocked",
            NativeSmartCoordinatorReason.AwaitingStability => "awaiting_stability",
            NativeSmartCoordinatorReason.AppliedMatchesDesired => "applied_matches_desired",
            NativeSmartCoordinatorReason.CpuCapabilityMissing => "cpu_capability_missing",
            NativeSmartCoordinatorReason.GpuCapabilityMissing => "gpu_capability_missing",
            NativeSmartCoordinatorReason.CapabilityClamped => "capability_clamped",
            NativeSmartCoordinatorReason.CriticalFactChanged => "critical_fact_changed",
            NativeSmartCoordinatorReason.CriticalMembershipChanged => "critical_membership_changed",
            NativeSmartCoordinatorReason.StartupGraceActive => "startup_grace_active",
            NativeSmartCoordinatorReason.TargetDisappeared => "target_disappeared",
            NativeSmartCoordinatorReason.FeatureDisabled => "feature_disabled",
            NativeSmartCoordinatorReason.FeedbackFailed => "feedback_failed",
            NativeSmartCoordinatorReason.OwnershipLost => "ownership_lost",
            NativeSmartCoordinatorReason.StateUncertain => "state_uncertain",
            NativeSmartCoordinatorReason.RetryBackoff => "retry_backoff",
            NativeSmartCoordinatorReason.ScoreOnly => "score_only",
            NativeSmartCoordinatorReason.NoCompleteSnapshot => "no_complete_snapshot",
            NativeSmartCoordinatorReason.DuplicateCycle => "duplicate_cycle",
            NativeSmartCoordinatorReason.AtomicGroupCompensation => "atomic_group_compensation",
            NativeSmartCoordinatorReason.ReservationTimedOut => "reservation_timed_out",
            NativeSmartCoordinatorReason.Known => "known",
            _ => ((ulong)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(NativeSmartCoordinatorSnapshotRowKind value)
        => value switch
        {
            NativeSmartCoordinatorSnapshotRowKind.Process => "process",
            NativeSmartCoordinatorSnapshotRowKind.Software => "software",
            _ => ((int)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(NativeSmartCoordinatorProcessGrade value)
        => value switch
        {
            NativeSmartCoordinatorProcessGrade.Level4 => "level4",
            NativeSmartCoordinatorProcessGrade.Level3 => "level3",
            NativeSmartCoordinatorProcessGrade.Level2 => "level2",
            NativeSmartCoordinatorProcessGrade.Level1 => "level1",
            NativeSmartCoordinatorProcessGrade.Normal => "normal",
            NativeSmartCoordinatorProcessGrade.A1 => "a1",
            _ => ((int)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(NativeSmartCoordinatorAdapterGrade value)
        => value switch
        {
            NativeSmartCoordinatorAdapterGrade.Freeze => "freeze",
            NativeSmartCoordinatorAdapterGrade.Optimize => "optimize",
            NativeSmartCoordinatorAdapterGrade.Normal => "normal",
            NativeSmartCoordinatorAdapterGrade.Extreme => "extreme",
            _ => ((int)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(NativeSmartCoordinatorSnapshotRowFlags value)
        => value switch
        {
            NativeSmartCoordinatorSnapshotRowFlags.None => "none",
            NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned => "process_owned",
            NativeSmartCoordinatorSnapshotRowFlags.CpuOwned => "cpu_owned",
            NativeSmartCoordinatorSnapshotRowFlags.GpuOwned => "gpu_owned",
            NativeSmartCoordinatorSnapshotRowFlags.ProcessInflight => "process_inflight",
            NativeSmartCoordinatorSnapshotRowFlags.CpuInflight => "cpu_inflight",
            NativeSmartCoordinatorSnapshotRowFlags.GpuInflight => "gpu_inflight",
            NativeSmartCoordinatorSnapshotRowFlags.FreezeReady => "freeze_ready",
            NativeSmartCoordinatorSnapshotRowFlags.CriticalFact => "critical_fact",
            NativeSmartCoordinatorSnapshotRowFlags.Known => "known",
            _ => ((uint)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(NativeSmartCoordinatorInputValidity value)
        => value switch
        {
            NativeSmartCoordinatorInputValidity.None => "none",
            NativeSmartCoordinatorInputValidity.ProcessIdentity => "process_identity",
            NativeSmartCoordinatorInputValidity.SoftwareIdentity => "software_identity",
            NativeSmartCoordinatorInputValidity.SoftwareKind => "software_kind",
            NativeSmartCoordinatorInputValidity.BaseScore => "base_score",
            NativeSmartCoordinatorInputValidity.SurfaceFacts => "surface_facts",
            NativeSmartCoordinatorInputValidity.Eligibility => "eligibility",
            NativeSmartCoordinatorInputValidity.Protection => "protection",
            NativeSmartCoordinatorInputValidity.CpuCapabilities => "cpu_capabilities",
            NativeSmartCoordinatorInputValidity.GpuCapabilities => "gpu_capabilities",
            NativeSmartCoordinatorInputValidity.Metric => "metric",
            NativeSmartCoordinatorInputValidity.AppliedProcessGrade => "applied_process_grade",
            NativeSmartCoordinatorInputValidity.AppliedCpuGrade => "applied_cpu_grade",
            NativeSmartCoordinatorInputValidity.AppliedGpuGrade => "applied_gpu_grade",
            NativeSmartCoordinatorInputValidity.ProcessScore => "process_score",
            NativeSmartCoordinatorInputValidity.SoftwareScore => "software_score",
            NativeSmartCoordinatorInputValidity.ScoreMemberCount => "score_member_count",
            NativeSmartCoordinatorInputValidity.Known => "known",
            _ => ((ulong)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(HostManagerNonAdaptedMemoryProcessAction value)
        => value switch
        {
            HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints => "restore_owned_constraints",
            HostManagerNonAdaptedMemoryProcessAction.Optimize => "optimize",
            HostManagerNonAdaptedMemoryProcessAction.PagedFrozen => "paged_frozen",
            _ => ((int)value).ToString(CultureInfo.InvariantCulture)
        };

    internal static string Token(NativeSmartCoordinatorRuntimeState value)
        => value switch
        {
            NativeSmartCoordinatorRuntimeState.Unknown => HostManagerRuntimeStates.Unknown,
            NativeSmartCoordinatorRuntimeState.ForegroundFocused => HostManagerRuntimeStates.ForegroundFocused,
            NativeSmartCoordinatorRuntimeState.ForegroundUnfocused => HostManagerRuntimeStates.ForegroundUnfocused,
            NativeSmartCoordinatorRuntimeState.BackgroundWindow => HostManagerRuntimeStates.BackgroundWindow,
            NativeSmartCoordinatorRuntimeState.TrayOnly => HostManagerRuntimeStates.TrayOnly,
            NativeSmartCoordinatorRuntimeState.BackgroundProcess => HostManagerRuntimeStates.BackgroundProcess,
            NativeSmartCoordinatorRuntimeState.NotRunning => HostManagerRuntimeStates.NotRunning,
            _ => throw new InvalidDataException($"Unknown NativeSmartCoordinatorRuntimeState '{value}'.")
        };

    internal static string Token(NativeComputeScoringRuntimeState value)
        => value switch
        {
            NativeComputeScoringRuntimeState.Unknown => HostManagerRuntimeStates.Unknown,
            NativeComputeScoringRuntimeState.ForegroundFocused => HostManagerRuntimeStates.ForegroundFocused,
            NativeComputeScoringRuntimeState.ForegroundUnfocused => HostManagerRuntimeStates.ForegroundUnfocused,
            NativeComputeScoringRuntimeState.BackgroundWindow => HostManagerRuntimeStates.BackgroundWindow,
            NativeComputeScoringRuntimeState.TrayOnly => HostManagerRuntimeStates.TrayOnly,
            NativeComputeScoringRuntimeState.BackgroundProcess => HostManagerRuntimeStates.BackgroundProcess,
            NativeComputeScoringRuntimeState.NotRunning => HostManagerRuntimeStates.NotRunning,
            _ => throw new InvalidDataException($"Unknown NativeComputeScoringRuntimeState '{value}'.")
        };

    internal static string Token(NativeSmartCoordinatorSoftwareKind value)
        => value switch
        {
            NativeSmartCoordinatorSoftwareKind.Unknown => "unknown",
            NativeSmartCoordinatorSoftwareKind.Game => SoftwareKinds.Game,
            NativeSmartCoordinatorSoftwareKind.HighPerformance => SoftwareKinds.HighPerformance,
            NativeSmartCoordinatorSoftwareKind.WindowsSystem => SoftwareKinds.WindowsSystem,
            NativeSmartCoordinatorSoftwareKind.Adapted => SoftwareKinds.Adapted,
            NativeSmartCoordinatorSoftwareKind.Dependency => SoftwareManagementRoles.Dependency,
            NativeSmartCoordinatorSoftwareKind.Runtime => "runtime",
            NativeSmartCoordinatorSoftwareKind.Controlled => SoftwareKinds.Controlled,
            NativeSmartCoordinatorSoftwareKind.Managed => SoftwareKinds.Managed,
            NativeSmartCoordinatorSoftwareKind.Unattributed => SoftwareKinds.Unattributed,
            NativeSmartCoordinatorSoftwareKind.GeneralApplication => "general_application",
            _ => throw new InvalidDataException($"Unknown NativeSmartCoordinatorSoftwareKind '{value}'.")
        };

    internal static IReadOnlyList<string> FlagTokens<T>(T value, Func<T, string> token)
        where T : struct, Enum
    {
        var bits = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        // Emit individual named bits only, excluding None and composite Known masks.
        // Unnamed set bits are not guessed: the adjacent exact *Value preserves them.
        return Enum.GetValues<T>()
            .Where(flag =>
            {
                var bit = Convert.ToUInt64(flag, CultureInfo.InvariantCulture);
                return bit != 0 && (bit & (bit - 1)) == 0 && (bits & bit) != 0;
            })
            .Select(token)
            .ToArray();
    }
}
