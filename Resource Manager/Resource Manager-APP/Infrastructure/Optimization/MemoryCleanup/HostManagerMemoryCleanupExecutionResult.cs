namespace ResourceManager.App.Infrastructure.Optimization;

internal enum HostManagerMemoryCleanupRoundDisposition : byte
{
    Incomplete = 0,
    InvalidCapacitySample = 1,
    IncompleteCandidateProjection = 2,
    MissingSchedulingGeneration = 3,
    JournalReconcileFailed = 4,
    PlannerFailed = 5,
    CompleteNoDecision = 6,
    DeferredByCycleBudget = 7,
    JournalPrepareFailed = 8,
    BatchOutcomeUnknown = 9,
    JournalSettlementFailed = 10,
    CompleteKnownFull = 11,
    BlockedByPriorUnknownOutcome = 12,
    FinalAdmissionCapacityUnavailable = 13,
    FinalAdmissionCandidateProjectionIncomplete = 14,
    FinalAdmissionCaptureFailed = 15,
    DeferredByCurrentFacts = 16,
    FinalAdmissionWindowStateUnavailable = 17,
    DeferredByCurrentCapacity = 18,
    DeferredByCurrentPolicy = 19,
    FinalAdmissionCandidatesUnknown = 20,
    BlockedByValidationScope = 21,
    JournalWriterArmFailed = 22,
    ValidationEvidenceCapacityUnavailable = 23,
    ValidationEvidenceSettlementFailed = 24,
    PlannerConfigurationMismatch = 25,
    AwaitingNewHostedCapacityObservation = 26
}

internal readonly record struct HostManagerMemoryCleanupExecutionResult(
    HostManagerMemoryCleanupRoundDisposition Disposition,
    int CandidateCount,
    int BlockedCandidateCount,
    ulong PlannerStateRevision,
    int PlannedCount,
    int RequestedCount,
    int TerminalCount,
    int SucceededCount,
    long CompletedAtUtcTicks)
{
    // Previously isolated candidates stay blocked individually, not for the whole cycle.
    internal bool HasUnresolvedCurrentEffects =>
        Disposition is HostManagerMemoryCleanupRoundDisposition.BatchOutcomeUnknown
            or HostManagerMemoryCleanupRoundDisposition.JournalReconcileFailed
            or HostManagerMemoryCleanupRoundDisposition.JournalSettlementFailed
            or HostManagerMemoryCleanupRoundDisposition.JournalWriterArmFailed
            or HostManagerMemoryCleanupRoundDisposition.ValidationEvidenceSettlementFailed;

    internal bool CompletedNormalReleaseRound =>
        CandidateCount >= 0
        && BlockedCandidateCount == 0
        && (Disposition switch
        {
            HostManagerMemoryCleanupRoundDisposition.CompleteNoDecision =>
                PlannedCount == 0
                && RequestedCount == 0
                && TerminalCount == 0
                && SucceededCount == 0,
            HostManagerMemoryCleanupRoundDisposition.CompleteKnownFull =>
                PlannerStateRevision > 0
                && PlannedCount > 0
                && PlannedCount <= CandidateCount
                && RequestedCount == PlannedCount
                && TerminalCount == RequestedCount
                && SucceededCount >= 0
                && SucceededCount <= TerminalCount,
            _ => false
        });

    internal static HostManagerMemoryCleanupExecutionResult NotRun(
        HostManagerMemoryCleanupRoundDisposition disposition)
        => new(
            disposition,
            CandidateCount: 0,
            BlockedCandidateCount: 0,
            PlannerStateRevision: 0,
            PlannedCount: 0,
            RequestedCount: 0,
            TerminalCount: 0,
            SucceededCount: 0,
            CompletedAtUtcTicks: 0);

    internal static HostManagerMemoryCleanupExecutionResult CompleteNoDecision(
        int candidateCount,
        ulong plannerStateRevision)
        => new(
            HostManagerMemoryCleanupRoundDisposition.CompleteNoDecision,
            candidateCount,
            BlockedCandidateCount: 0,
            plannerStateRevision,
            PlannedCount: 0,
            RequestedCount: 0,
            TerminalCount: 0,
            SucceededCount: 0,
            CompletedAtUtcTicks: 0);

    internal static HostManagerMemoryCleanupExecutionResult CompleteKnownFull(
        int candidateCount,
        ulong plannerStateRevision,
        int count,
        int succeededCount)
        => new(
            HostManagerMemoryCleanupRoundDisposition.CompleteKnownFull,
            candidateCount,
            BlockedCandidateCount: 0,
            plannerStateRevision,
            count,
            count,
            count,
            succeededCount,
            CompletedAtUtcTicks: 0);

    internal static HostManagerMemoryCleanupExecutionResult Incomplete(
        HostManagerMemoryCleanupRoundDisposition disposition =
            HostManagerMemoryCleanupRoundDisposition.Incomplete,
        int candidateCount = 0,
        int blockedCandidateCount = 0,
        ulong plannerStateRevision = 0,
        int plannedCount = 0,
        int requestedCount = 0,
        int terminalCount = 0,
        int succeededCount = 0)
        => new(
            disposition,
            candidateCount,
            blockedCandidateCount,
            plannerStateRevision,
            plannedCount,
            requestedCount,
            terminalCount,
            succeededCount,
            CompletedAtUtcTicks: 0);

    internal HostManagerMemoryCleanupExecutionResult StampCompletion(
        DateTimeOffset completedAt)
        => CompletedNormalReleaseRound && completedAt.UtcTicks > 0
            ? this with { CompletedAtUtcTicks = completedAt.UtcTicks }
            : this;
}
