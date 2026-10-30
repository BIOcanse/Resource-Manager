using System.Globalization;
using ResourceManager.App.Domain.Optimization;
using static ResourceManager.App.Infrastructure.Optimization.HostManagerDecisionDiagnosticTokens;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private HostManagerCycleActionBudgetDiagnostics? lastCycleActionBudget;
    private NativeSmartCoordinatorStatus? cycleNativePlanFailure;
    private readonly ResourceManager.ReadWrite.Publications.CurrentValuePublicationSignal decisionPublications = new();

    // Coordinator-owned derived current value, never a native workspace borrow.
    // Replaced only after the segment has settled, while its caller owns gate.
    private sealed record PublishedObservation(
        NativeSmartCoordinatorSnapshot? NativeSnapshot,
        NativeSmartCoordinatorSnapshotRow[] NativeRows,
        HostManagerDecisionDiagnosticsSnapshot Decisions);

    private PublishedObservation publishedObservation = new(null, [], new(
        "resource-manager-smart-decision-diagnostics-v2",
        DateTimeOffset.UtcNow,
        runtimePlanProvider.Current.Version,
        runtimePlanProvider.Current.OptimizationMode.Mode,
        false,
        ProjectSchedulingAuthorityDiagnostics(schedulingAuthority.Capture()),
        null,
        []));

    internal Task<HostManagerDecisionDiagnosticsSnapshot>
        GetDecisionDiagnosticsAsync(CancellationToken cancellationToken)
    {
        RequireOperationAdmission();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref publishedObservation).Decisions);
    }

    internal async IAsyncEnumerable<HostManagerDecisionDiagnosticsSnapshot> SubscribeDecisionDiagnosticsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var publication = decisionPublications.Subscribe();
        var current = await GetDecisionDiagnosticsAsync(cancellationToken);
        yield return current;
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            var next = await GetDecisionDiagnosticsAsync(cancellationToken);
            // A publication racing the initial read may already be included in current.
            if (ReferenceEquals(current, next)) continue;
            current = next;
            yield return current;
        }
    }

    private void PublishObservation(HostManagerCycleFailureDiagnostics? failure = null)
    {
        var runtimePlan = runtimePlanProvider.Current;
        var authority = schedulingAuthorityOwner.Capture();
        if (failure is not null)
        {
            authority = authority with
            {
                Availability = HostManagerSchedulingAuthorityAvailability.Unavailable,
                ObservedAt = DateTimeOffset.UtcNow,
                Compute = null,
                MemoryModes = null,
                PolicyEvidence = null,
                UnavailableReason = "scheduler-cycle-failed"
            };
        }
        var memoryProjection = failure is null ? NonAdaptedMemoryModeProjection : null;
        // Failure may leave an unvalidated or incomplete workspace snapshot.
        // Publish its typed empty value without borrowing/projecting that data.
        var workspace = failure is null ? nativeWorkspace : null;
        var snapshot = workspace?.Snapshot;
        var rows = workspace?.CurrentSnapshotRows.ToArray() ?? [];
        var schedulingEnabled = runtimePlan.OptimizationMode.SchedulingEnabled;
        var native = !schedulingEnabled || workspace is null
            ? null
            : ProjectNativeDiagnostics(
                snapshot!.Value,
                rows);

        var decisions = new HostManagerDecisionDiagnosticsSnapshot(
            "resource-manager-smart-decision-diagnostics-v2",
            DateTimeOffset.UtcNow,
            runtimePlan.Version,
            runtimePlan.OptimizationMode.Mode,
            schedulerRunning && schedulingEnabled && failure is null,
            ProjectSchedulingAuthorityDiagnostics(authority),
            native,
            ProjectNonAdaptedMemoryDiagnostics(memoryProjection))
        {
            Failure = failure,
            ActionBudget = lastCycleActionBudget,
            NonAdaptedMemorySkipCounts = failure is null ? ProjectMemoryDirectiveSkipCounts() : [],
            FactExclusionCount = cycleFactExclusions.Count,
            FactExclusionCounts = cycleFactExclusions.GroupBy(e => (e.Fact, e.Reason))
                .Select(g => new HostManagerFactExclusionCount(g.Key.Fact, g.Key.Reason, g.Count())).ToArray(),
            FactExclusions = cycleFactExclusions.Take(64).Select(e => new HostManagerFactExclusionDiagnostics(
                e.ProcessId, e.ProcessStartKey?.ToString(CultureInfo.InvariantCulture), e.TargetId, e.Fact, e.Reason, e.AdapterKey?.ToString(CultureInfo.InvariantCulture))).ToArray()
        };
        Volatile.Write(ref publishedObservation, new(snapshot, rows, decisions));
        decisionPublications.Publish();
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
            Exact(authority.RoundNumber),
            Token(authority.Availability),
            authority.ObservedAt == default ? null : authority.ObservedAt,
            authority.UnavailableReason,
            binding,
            authority.Compute?.CpuOccupancySource is { } source ? Token(source) : null,
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
            Exact(domain.RoundNumber),
            domain.Scores.Select(static score =>
                new HostManagerComputeScoreDiagnostics(
                    Token(score.Kind),
                    Exact(score.RoundNumber),
                    Exact(score.TargetKey),
                    Exact(score.SoftwareKey),
                    score.ProcessId,
                    Exact(score.ProcessStartKey),
                    Exact(score.AdapterKey),
                    score.Score,
                    score.MemberCount,
                    Token(score.RuntimeState)))
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
            Exact(memoryModes.RoundNumber),
            memoryModes.UnrestrictedAllowed,
            memoryModes.OptimizeCount,
            memoryModes.StrongestCount,
            memoryModes.Software.Select(static desired =>
                new HostManagerDesiredMemoryModeDiagnostics(
                    Exact(desired.SoftwareKey),
                    Exact(desired.RoundNumber),
                    desired.CpuScore,
                    desired.Rank,
                    Token(desired.Mode),
                    desired.BaseScore,
                    desired.BaseScoreClamped,
                    Enum.GetValues<NativeMemoryGradeSet>()
                        .Where(grade => grade is not (NativeMemoryGradeSet.None or NativeMemoryGradeSet.Known)
                            && (desired.BaseScoreAllowedGrades & grade) != 0)
                        .Select(static grade => Token(grade))
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
            snapshot.EventBoostRoundsRemaining,
            snapshot.ActionCount,
            snapshot.ProcessCount,
            snapshot.SoftwareCount,
            snapshot.PendingCount,
            snapshot.InflightCount,
            snapshot.InvalidFactCount,
            FlagTokens(snapshot.Flags, Token),
            Exact((uint)snapshot.Flags),
            FlagTokens(snapshot.ReasonMask, Token),
            Exact((ulong)snapshot.ReasonMask),
            rows.Select(ProjectNativeRowDiagnostics).ToArray());
    }

    private static HostManagerNativeCoordinatorRowDiagnostics
        ProjectNativeRowDiagnostics(NativeSmartCoordinatorSnapshotRow row)
        => new(
            Token(row.RowKind),
            Exact(row.TargetKey),
            Exact(row.SoftwareKey),
            row.ProcessId,
            Exact(row.ProcessStartKey),
            row.SourceIndex,
            Token(row.SoftwareKind),
            Token(row.RuntimeState),
            row.ProtectionLevel,
            row.BaseScore,
            row.CpuScore,
            row.CpuOccupancyPercent,
            Token(row.DesiredProcessGrade),
            Token(row.AppliedProcessGrade),
            Token(row.PendingProcessGrade),
            row.ProcessPendingCount,
            row.FailureCount,
            FlagTokens(row.Flags, Token),
            Exact((uint)row.Flags),
            FlagTokens(row.ValidMask, Token),
            Exact((ulong)row.ValidMask),
            FlagTokens(row.ReasonMask, Token),
            Exact((ulong)row.ReasonMask),
            Exact(row.LastSeenCycle),
            row.PendingFirstSeenMilliseconds,
            row.PendingLastSeenMilliseconds,
            row.LastFeedbackMilliseconds,
            row.GameStartedAtMilliseconds);

    private IReadOnlyList<HostManagerNonAdaptedMemoryDecisionDiagnostics>
        ProjectNonAdaptedMemoryDiagnostics(
            HostManagerNonAdaptedMemoryModeProjectionSnapshot? projection)
    {
        if (projection is null)
        {
            return [];
        }

        return projection.Processes.Select(directive =>
            new HostManagerNonAdaptedMemoryDecisionDiagnostics(
                Exact(directive.RoundNumber),
                Exact(directive.SoftwareKey),
                directive.SoftwareId ?? string.Empty,
                directive.ProcessId,
                Exact(directive.ProcessStartKey),
                Exact(directive.MemoryPolicyTargetKey),
                directive.SoftwareRank,
                Token(directive.Mode),
                Token(directive.Action),
                directive.TargetMemoryPriority)
            {
                TransactionPrepared = GetMemoryDirectiveOutcome(directive).TransactionPrepared,
                SkipReason = GetMemoryDirectiveOutcome(directive).SkipReason
            })
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
        NonAdaptedMemoryDecisions)
{
    public HostManagerCycleFailureDiagnostics? Failure { get; init; }
    public IReadOnlyList<HostManagerMemoryDirectiveSkipDiagnostics> NonAdaptedMemorySkipCounts { get; init; } = [];
    public HostManagerCycleActionBudgetDiagnostics? ActionBudget { get; init; }
    public int FactExclusionCount { get; init; }
    public IReadOnlyList<HostManagerFactExclusionCount> FactExclusionCounts { get; init; } = [];
    public IReadOnlyList<HostManagerFactExclusionDiagnostics> FactExclusions { get; init; } = [];
}

internal sealed record HostManagerCycleActionBudgetDiagnostics(
    uint? MaximumActionsThisCycle,
    bool RealtimeCycle,
    string Mode,
    int Count,
    int Percent,
    int? ProcessCount,
    string? DeferredReason = null)
{
    public int Value => Mode == "fixed" ? Count : Percent;
}

internal sealed record HostManagerSchedulingAuthorityDiagnostics(
    string RoundNumber,
    string Availability,
    DateTimeOffset? ObservedAt,
    string? UnavailableReason,
    HostManagerSchedulingPlanBindingDiagnostics? PlanBinding,
    string? CpuOccupancySource,
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
    string RoundNumber,
    IReadOnlyList<HostManagerComputeScoreDiagnostics> Scores);

internal sealed record HostManagerComputeScoreDiagnostics(
    string Kind,
    string RoundNumber,
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
    string RoundNumber,
    bool UnrestrictedAllowed,
    uint OptimizeCount,
    uint StrongestCount,
    IReadOnlyList<HostManagerDesiredMemoryModeDiagnostics> Software);

internal sealed record HostManagerDesiredMemoryModeDiagnostics(
    string SoftwareKey,
    string RoundNumber,
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
    uint EventBoostRoundsRemaining,
    uint ActionCount,
    uint ProcessCount,
    uint SoftwareCount,
    uint PendingCount,
    uint InflightCount,
    uint InvalidFactCount,
    IReadOnlyList<string> Flags,
    string FlagsValue,
    IReadOnlyList<string> ReasonMask,
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
    string DesiredProcessGrade,
    string AppliedProcessGrade,
    string PendingProcessGrade,
    uint ProcessPendingCount,
    uint FailureCount,
    IReadOnlyList<string> Flags,
    string FlagsValue,
    IReadOnlyList<string> ValidMask,
    string ValidMaskValue,
    IReadOnlyList<string> ReasonMask,
    string ReasonMaskValue,
    string LastSeenCycle,
    long PendingFirstSeenMilliseconds,
    long PendingLastSeenMilliseconds,
    long LastFeedbackMilliseconds,
    long GameStartedAtMilliseconds);

internal sealed record HostManagerNonAdaptedMemoryDecisionDiagnostics(
    string RoundNumber,
    string SoftwareKey,
    string SoftwareId,
    int ProcessId,
    string ProcessStartKey,
    string MemoryPolicyTargetKey,
    uint SoftwareRank,
    string Mode,
    string Action,
    uint? TargetMemoryPriority)
{
    public bool TransactionPrepared { get; init; }
    public string? SkipReason { get; init; }
}

internal sealed record HostManagerMemoryDirectiveSkipDiagnostics(
    string Reason, int Count, IReadOnlyList<string> ExampleTargetIds);
