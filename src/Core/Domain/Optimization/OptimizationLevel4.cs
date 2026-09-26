namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationLevel4ActionKinds
{
    public const string FreezeProcess = "FreezeProcess";
}

public static class OptimizationLevel4RecordStates
{
    public const string Active = "Active";
    public const string Restored = "Restored";
    public const string PartiallyRestored = "PartiallyRestored";
    public const string NoChanges = "NoChanges";
}

public sealed record OptimizationLevel4Request(
    string ReportId,
    bool ConfirmOperation);

public sealed record OptimizationLevel4RestoreRequest(
    string RecordId,
    bool ConfirmOperation);

public sealed record OptimizationLevel4Preview(
    string ReportId,
    DateTimeOffset CapturedAt,
    bool Eligible,
    string Summary,
    string? DisabledReason,
    OptimizationReportTarget Target,
    IReadOnlyList<OptimizationLevel4ActionPreview> Actions);

public sealed record OptimizationLevel4ActionPreview(
    string Id,
    string Kind,
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    DateTimeOffset? ProcessStartedAt,
    int ThreadCount,
    string CurrentValue,
    string ProposedValue,
    bool WillChange,
    bool Enabled,
    string? DisabledReason,
    IReadOnlyList<string> Details);

public sealed record OptimizationLevel4ApplyResult(
    string ReportId,
    string State,
    string Message,
    OptimizationLevel4Preview Preview,
    OptimizationLevel4Record? Record);

public sealed record OptimizationLevel4RestoreResult(
    string RecordId,
    string State,
    string Message,
    int RestoredCount,
    int SkippedCount,
    OptimizationLevel4Record Record);

public sealed record OptimizationLevel4Record(
    string Id,
    string ReportId,
    string TargetKey,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string State,
    IReadOnlyList<OptimizationLevel4AppliedAction> Actions);

public sealed record OptimizationLevel4AppliedAction(
    string Id,
    string Kind,
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    DateTimeOffset? ProcessStartedAt,
    IReadOnlyList<ProcessThreadSuspendRecord> Threads,
    bool WorkingSetTrimAttempted,
    bool WorkingSetTrimSucceeded,
    string? WorkingSetTrimMessage,
    DateTimeOffset AppliedAt,
    DateTimeOffset? RestoredAt,
    string State,
    string? Message);

public sealed record ProcessFreezeTarget(
    ProcessResourcePolicySnapshot Process,
    IReadOnlyList<int> ThreadIds);

public sealed record ProcessThreadSuspendRecord(
    int ThreadId,
    uint PreviousSuspendCount,
    DateTimeOffset SuspendedAt,
    string? Message);

public sealed record ProcessThreadResumeRecord(
    int ThreadId,
    bool Succeeded,
    uint? PreviousSuspendCount,
    string Message);

public sealed record ProcessFreezeWriteResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<ProcessThreadSuspendRecord> SuspendedThreads,
    bool WorkingSetTrimAttempted,
    bool WorkingSetTrimSucceeded,
    string? WorkingSetTrimMessage);

public sealed record ProcessFreezeRestoreResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<ProcessThreadResumeRecord> Threads);
