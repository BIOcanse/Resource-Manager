namespace ResourceManager.App.Domain.Optimization;

public sealed record HostManagerSmartCoordinatorStatus(
    string Mode,
    bool SchedulerRunning,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? LastRestoreAt,
    int PendingChangeCount,
    int AppliedTargetCount,
    string Message);

public sealed record HostManagerSmartCoordinatorModeRequest(
    string Mode);
