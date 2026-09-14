using System.Globalization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    internal async Task<HostManagerDecisionDiagnosticsSnapshot>
        GetDecisionDiagnosticsAsync(CancellationToken cancellationToken)
    {
        RequireOperationAdmission();
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireOperationAdmission();
            var runtimePlan = runtimePlanProvider.Current;
            var authority = schedulingAuthorityOwner.Capture();
            var memoryProjection = NonAdaptedMemoryModeProjection;
            var workspace = nativeWorkspace;
            var native = workspace is null
                ? null
                : ProjectNativeDiagnostics(
                    workspace.Snapshot,
                    workspace.CurrentSnapshotRows.ToArray());

            return new HostManagerDecisionDiagnosticsSnapshot(
                "resource-manager-smart-decision-diagnostics-v1",
                DateTimeOffset.UtcNow,
                runtimePlan.Version,
                runtimePlan.OptimizationMode.Mode,
                schedulerRunning,
                ProjectSchedulingAuthorityDiagnostics(authority),
                native,
                ProjectNonAdaptedMemoryDiagnostics(memoryProjection));
        }
        finally
        {
            gate.Release();
        }
    }

    private static HostManagerSchedulingAuthorityDiagnostics
        ProjectSchedulingAuthorityDiagnostics(
            HostManagerSchedulingAuthoritySnapshot authority)
    {
        var binding = authority.PlanBinding is null
            ? null
            : new HostManagerSchedulingPlanBindingDiagnostics(
                Exact(authority.PlanBinding.HostPublicationSequence),
                Exact(authority.PlanBinding.HostPlanEpoch),
                authority.PlanBinding.HostPlanSha256,
                Exact(authority.PlanBinding.SmartConfigurationGeneration),
                authority.PlanBinding.SmartConfigurationSha256,
                authority.PlanBinding.MemoryModePolicyEnabled,
                authority.PlanBinding.MemoryModePolicySourceKind,
                Exact(authority.PlanBinding.MemoryModeConfigurationGeneration),
                authority.PlanBinding.MemoryModeConfigurationSha256);
        var policy = authority.PolicyEvidence is null
            ? null
            : new HostManagerMemoryModePolicyEvidenceDiagnostics(
                authority.PolicyEvidence.SourceKind,
                authority.PolicyEvidence.ConfigurationSha256,
                authority.PolicyEvidence.BindingSha256);

        return new HostManagerSchedulingAuthorityDiagnostics(
            Exact(authority.AttemptGeneration),
            authority.Availability.ToString(),
            authority.ObservedAt == default ? null : authority.ObservedAt,
            authority.UnavailableReason,
            binding,
            ProjectComputeDomainDiagnostics(authority.Compute?.Cpu),
            ProjectComputeDomainDiagnostics(authority.Compute?.Gpu),
            ProjectMemoryModeDiagnostics(authority.MemoryModes),
            policy);
    }

    private static HostManagerComputeDomainDiagnostics?
        ProjectComputeDomainDiagnostics(
            HostManagerComputeScoreDomainSnapshot? domain)
    {
        if (domain is null)
        {
            return null;
        }

        return new HostManagerComputeDomainDiagnostics(
            Exact(domain.SchedulingGeneration),
            Exact(domain.SourceGeneration),
            Exact(domain.TopologyGeneration),
            Exact(domain.TopologyFingerprint),
            domain.Scores.Select(static score =>
                new HostManagerComputeScoreDiagnostics(
                    score.Kind.ToString(),
                    Exact(score.SchedulingGeneration),
                    Exact(score.TargetKey),
                    Exact(score.SoftwareKey),
                    score.ProcessId,
                    Exact(score.ProcessStartKey),
                    Exact(score.AdapterKey),
                    score.Score,
                    score.MemberCount,
                    score.RuntimeState.ToString()))
                .ToArray());
    }

    internal static HostManagerMemoryModeDiagnostics? ProjectMemoryModeDiagnostics(
        HostManagerMemoryModeDesiredSnapshot? memoryModes)
    {
        if (memoryModes is null)
        {
            return null;
        }

        return new HostManagerMemoryModeDiagnostics(
            Exact(memoryModes.ConfigurationGeneration),
            Exact(memoryModes.SchedulingGeneration),
            Exact(memoryModes.MemorySource.WorkspaceIdentity),
            Exact(memoryModes.MemorySource.CommittedGeneration),
            Exact(memoryModes.SnapshotGeneration),
            memoryModes.UnrestrictedAllowed,
            memoryModes.OptimizeCount,
            memoryModes.StrongestCount,
            memoryModes.Software.Select(static desired =>
                new HostManagerDesiredMemoryModeDiagnostics(
                    Exact(desired.SoftwareKey),
                    Exact(desired.SchedulingGeneration),
                    Exact(desired.SnapshotGeneration),
                    desired.CpuScore,
                    desired.Rank,
                    desired.Mode.ToString(),
                    desired.BaseScore,
                    desired.BaseScoreClamped,
                    Enum.GetValues<NativeMemoryGradeSet>()
                        .Where(grade => grade is not (NativeMemoryGradeSet.None or NativeMemoryGradeSet.Known)
                            && (desired.BaseScoreAllowedGrades & grade) != 0)
                        .Select(static grade => grade.ToString())
                        .ToArray()))
                .ToArray());
    }

    internal static HostManagerNativeCoordinatorDiagnostics ProjectNativeDiagnostics(
        NativeSmartCoordinatorSnapshot snapshot,
        IReadOnlyList<NativeSmartCoordinatorSnapshotRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (snapshot.SnapshotRowCount != rows.Count)
        {
            throw new InvalidDataException(
                "The native diagnostics rows do not match the current snapshot count.");
        }

        return new HostManagerNativeCoordinatorDiagnostics(
            Exact(snapshot.ConfigurationGeneration),
            Exact(snapshot.CycleSequence),
            Exact(snapshot.PlanEpoch),
            Exact(snapshot.StateRevision),
            snapshot.ObservedAtMilliseconds,
            snapshot.NextWakeAtMilliseconds,
            snapshot.WakeAfterMilliseconds,
            snapshot.ActionCount,
            snapshot.ProcessCount,
            snapshot.SoftwareCount,
            snapshot.PendingCount,
            snapshot.InflightCount,
            snapshot.InvalidFactCount,
            snapshot.Flags.ToString(),
            Exact((uint)snapshot.Flags),
            snapshot.ReasonMask.ToString(),
            Exact((ulong)snapshot.ReasonMask),
            rows.Select(ProjectNativeRowDiagnostics).ToArray());
    }

    private static HostManagerNativeCoordinatorRowDiagnostics
        ProjectNativeRowDiagnostics(NativeSmartCoordinatorSnapshotRow row)
        => new(
            row.RowKind.ToString(),
            Exact(row.TargetKey),
            Exact(row.SoftwareKey),
            row.ProcessId,
            Exact(row.ProcessStartKey),
            row.SourceIndex,
            row.SoftwareKind.ToString(),
            row.RuntimeState.ToString(),
            row.ProtectionLevel,
            row.BaseScore,
            row.CpuScore,
            row.CpuOccupancyPercent,
            row.AdapterCpuScore,
            row.AdapterGpuScore,
            row.DesiredProcessGrade.ToString(),
            row.AppliedProcessGrade.ToString(),
            row.PendingProcessGrade.ToString(),
            row.DesiredCpuGrade.ToString(),
            row.AppliedCpuGrade.ToString(),
            row.PendingCpuGrade.ToString(),
            row.DesiredGpuGrade.ToString(),
            row.AppliedGpuGrade.ToString(),
            row.PendingGpuGrade.ToString(),
            row.ProcessPendingCount,
            row.CpuPendingCount,
            row.GpuPendingCount,
            row.FailureCount,
            row.CpuCapabilityMask,
            row.GpuCapabilityMask,
            row.Flags.ToString(),
            Exact((uint)row.Flags),
            row.ValidMask.ToString(),
            Exact((ulong)row.ValidMask),
            row.ReasonMask.ToString(),
            Exact((ulong)row.ReasonMask),
            Exact(row.LastSeenCycle),
            row.PendingFirstSeenMilliseconds,
            row.PendingLastSeenMilliseconds,
            row.LastFeedbackMilliseconds,
            row.GameStartedAtMilliseconds);

    private static IReadOnlyList<HostManagerNonAdaptedMemoryDecisionDiagnostics>
        ProjectNonAdaptedMemoryDiagnostics(
            HostManagerNonAdaptedMemoryModeProjectionSnapshot? projection)
    {
        if (projection is null)
        {
            return [];
        }

        return projection.Processes.Select(static directive =>
            new HostManagerNonAdaptedMemoryDecisionDiagnostics(
                Exact(directive.SchedulingGeneration),
                Exact(directive.SoftwareKey),
                directive.SoftwareId ?? string.Empty,
                directive.ProcessId,
                Exact(directive.ProcessStartKey),
                Exact(directive.MemoryPolicyTargetKey),
                directive.SoftwareRank,
                directive.Mode.ToString(),
                directive.Action.ToString(),
                directive.TargetMemoryPriority))
            .ToArray();
    }

    private static string Exact(ulong value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static string Exact(uint value)
        => value.ToString(CultureInfo.InvariantCulture);
}

internal sealed record HostManagerDecisionDiagnosticsSnapshot(
    string Contract,
    DateTimeOffset CapturedAtUtc,
    long RuntimePlanVersion,
    string Mode,
    bool SchedulerRunning,
    HostManagerSchedulingAuthorityDiagnostics SchedulingAuthority,
    HostManagerNativeCoordinatorDiagnostics? NativeCoordinator,
    IReadOnlyList<HostManagerNonAdaptedMemoryDecisionDiagnostics>
        NonAdaptedMemoryDecisions);

internal sealed record HostManagerSchedulingAuthorityDiagnostics(
    string AttemptGeneration,
    string Availability,
    DateTimeOffset? ObservedAt,
    string? UnavailableReason,
    HostManagerSchedulingPlanBindingDiagnostics? PlanBinding,
    HostManagerComputeDomainDiagnostics? Cpu,
    HostManagerComputeDomainDiagnostics? Gpu,
    HostManagerMemoryModeDiagnostics? MemoryModes,
    HostManagerMemoryModePolicyEvidenceDiagnostics? PolicyEvidence);

internal sealed record HostManagerSchedulingPlanBindingDiagnostics(
    string HostPublicationSequence,
    string HostPlanEpoch,
    string HostPlanSha256,
    string SmartConfigurationGeneration,
    string SmartConfigurationSha256,
    bool MemoryModePolicyEnabled,
    string MemoryModePolicySourceKind,
    string MemoryModeConfigurationGeneration,
    string MemoryModeConfigurationSha256);

internal sealed record HostManagerMemoryModePolicyEvidenceDiagnostics(
    string SourceKind,
    string ConfigurationSha256,
    string BindingSha256);

internal sealed record HostManagerComputeDomainDiagnostics(
    string SchedulingGeneration,
    string SourceGeneration,
    string TopologyGeneration,
    string TopologyFingerprint,
    IReadOnlyList<HostManagerComputeScoreDiagnostics> Scores);

internal sealed record HostManagerComputeScoreDiagnostics(
    string Kind,
    string SchedulingGeneration,
    string TargetKey,
    string SoftwareKey,
    int ProcessId,
    string ProcessStartKey,
    string AdapterKey,
    double Score,
    uint MemberCount,
    string RuntimeState);

internal sealed record HostManagerMemoryModeDiagnostics(
    string ConfigurationGeneration,
    string SchedulingGeneration,
    string MemoryWorkspaceIdentity,
    string MemoryCommittedGeneration,
    string SnapshotGeneration,
    bool UnrestrictedAllowed,
    uint OptimizeCount,
    uint StrongestCount,
    IReadOnlyList<HostManagerDesiredMemoryModeDiagnostics> Software);

internal sealed record HostManagerDesiredMemoryModeDiagnostics(
    string SoftwareKey,
    string SchedulingGeneration,
    string SnapshotGeneration,
    double CpuScore,
    uint Rank,
    string Mode,
    double BaseScore,
    bool BaseScoreClamped,
    IReadOnlyList<string> BaseScoreAllowedGrades);

internal sealed record HostManagerNativeCoordinatorDiagnostics(
    string ConfigurationGeneration,
    string CycleSequence,
    string PlanEpoch,
    string StateRevision,
    long ObservedAtMilliseconds,
    long NextWakeAtMilliseconds,
    uint WakeAfterMilliseconds,
    uint ActionCount,
    uint ProcessCount,
    uint SoftwareCount,
    uint PendingCount,
    uint InflightCount,
    uint InvalidFactCount,
    string Flags,
    string FlagsValue,
    string ReasonMask,
    string ReasonMaskValue,
    IReadOnlyList<HostManagerNativeCoordinatorRowDiagnostics> Rows);

internal sealed record HostManagerNativeCoordinatorRowDiagnostics(
    string RowKind,
    string TargetKey,
    string SoftwareKey,
    uint ProcessId,
    string ProcessStartKey,
    uint SourceIndex,
    string SoftwareKind,
    string RuntimeState,
    byte ProtectionLevel,
    double BaseScore,
    double CpuScore,
    double CpuOccupancyPercent,
    double AdapterCpuScore,
    double AdapterGpuScore,
    string DesiredProcessGrade,
    string AppliedProcessGrade,
    string PendingProcessGrade,
    string DesiredCpuGrade,
    string AppliedCpuGrade,
    string PendingCpuGrade,
    string DesiredGpuGrade,
    string AppliedGpuGrade,
    string PendingGpuGrade,
    uint ProcessPendingCount,
    uint CpuPendingCount,
    uint GpuPendingCount,
    uint FailureCount,
    byte CpuCapabilityMask,
    byte GpuCapabilityMask,
    string Flags,
    string FlagsValue,
    string ValidMask,
    string ValidMaskValue,
    string ReasonMask,
    string ReasonMaskValue,
    string LastSeenCycle,
    long PendingFirstSeenMilliseconds,
    long PendingLastSeenMilliseconds,
    long LastFeedbackMilliseconds,
    long GameStartedAtMilliseconds);

internal sealed record HostManagerNonAdaptedMemoryDecisionDiagnostics(
    string SchedulingGeneration,
    string SoftwareKey,
    string SoftwareId,
    int ProcessId,
    string ProcessStartKey,
    string MemoryPolicyTargetKey,
    uint SoftwareRank,
    string Mode,
    string Action,
    uint? TargetMemoryPriority);
