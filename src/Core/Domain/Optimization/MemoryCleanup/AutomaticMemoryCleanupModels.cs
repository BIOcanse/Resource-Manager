namespace ResourceManager.App.Domain.Optimization.MemoryCleanup;

[Flags]
public enum AutomaticMemoryCleanupRequestKind : byte
{
    None = 0,
    Normal = 1 << 0,
    EvaluateEmergency = 1 << 1,
    ForceEmergency = 1 << 2
}

public enum AutomaticMemoryCleanupMode : byte
{
    Normal = 0,
    Emergency = 1
}

public sealed record AutomaticMemoryCleanupCandidate(
    string TargetId,
    string DisplayName,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    string RuntimeState,
    double BaseScore,
    double CpuScore,
    double MemoryUsedPercent,
    bool CanApply);

public sealed record AutomaticMemoryCleanupPlanRequest(
    AutomaticMemoryCleanupRequestKind Kind,
    double OrdinaryMemoryFreeRatio,
    double PhysicalMemoryFreeRatio,
    double VirtualMemoryFreeRatio,
    IReadOnlyList<AutomaticMemoryCleanupCandidate> Candidates);

public readonly record struct AutomaticMemoryCleanupReservation(
    uint StateSlot,
    uint StateGeneration);

public sealed record AutomaticMemoryCleanupDecision(
    int SourceInputIndex,
    AutomaticMemoryCleanupCandidate Candidate,
    AutomaticMemoryCleanupMode Mode,
    AutomaticMemoryCleanupReservation Reservation);

public sealed record AutomaticMemoryCleanupPlanResult(
    IReadOnlyList<AutomaticMemoryCleanupDecision> Decisions,
    ulong StateRevision,
    ulong ConfigurationGeneration = 0)
{
    public static AutomaticMemoryCleanupPlanResult Empty { get; } = new([], 0, 0);
}

public sealed record AutomaticMemoryCleanupFeedback(
    AutomaticMemoryCleanupReservation Reservation,
    bool Attempted,
    bool Succeeded);
