namespace ResourceManager.App.Domain.Operations;

public static class HostManagerOperationStates
{
    public const string Queued = "queued";
    public const string StartPending = "startPending";
    public const string Running = "running";
    public const string CancelPending = "cancelPending";
    public const string RetryWait = "retryWait";
    public const string RecoveryPending = "recoveryPending";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
    public const string StateUncertain = "stateUncertain";

    public static bool IsTerminal(string state)
        => state is Succeeded or Failed or Canceled or StateUncertain;
}

public sealed record HostManagerOperationSnapshot(
    string Id,
    string Kind,
    string? DomainKey,
    string? Title,
    string State,
    ulong ConfigurationGeneration,
    ulong StateRevision,
    uint AttemptNumber,
    uint MaximumAttempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    bool CancelRequested,
    HostManagerOperationProgress? Progress,
    string? Result,
    string? Error);

public sealed record HostManagerOperationCoordinatorHealth(
    bool Ready,
    bool PersistenceFaulted,
    string? FaultStage,
    string? FaultMessage,
    ulong PublicationRevision,
    ulong ConfigurationGeneration);

public sealed record HostManagerOperationsPublishedState(
    DateTimeOffset CapturedAt,
    ulong PublicationRevision,
    ulong ConfigurationGeneration,
    HostManagerOperationCoordinatorHealth Health,
    IReadOnlyList<HostManagerOperationSnapshot> Operations);
