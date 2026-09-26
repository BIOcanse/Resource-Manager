namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationLevel3ActionKinds
{
    public const string DeepLowerProcessPriority = "DeepLowerProcessPriority";
    public const string EnableExecutionSpeedThrottling = "EnableExecutionSpeedThrottling";
    public const string LowerMemoryPriority = "LowerMemoryPriority";
    public const string MoveProcessAffinity = "MoveProcessAffinity";
}

public static class OptimizationLevel3RecordStates
{
    public const string Active = "Active";
    public const string Restored = "Restored";
    public const string PartiallyRestored = "PartiallyRestored";
    public const string NoChanges = "NoChanges";
}

public sealed record OptimizationLevel3Request(
    string ReportId,
    bool ConfirmOperation);

public sealed record OptimizationLevel3RestoreRequest(
    string RecordId,
    bool ConfirmOperation);

public sealed record OptimizationLevel3Preview(
    string ReportId,
    DateTimeOffset CapturedAt,
    bool Eligible,
    string Summary,
    string? DisabledReason,
    OptimizationReportTarget Target,
    CpuAffinityPlan AffinityPlan,
    IReadOnlyList<OptimizationLevel3ActionPreview> Actions);

public sealed record OptimizationLevel3ActionPreview(
    string Id,
    string Kind,
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    DateTimeOffset? ProcessStartedAt,
    string CurrentValue,
    string ProposedValue,
    string? CurrentRawValue,
    string ProposedRawValue,
    bool WillChange,
    bool Enabled,
    string? DisabledReason,
    IReadOnlyList<string> Details);

public sealed record OptimizationLevel3ApplyResult(
    string ReportId,
    string State,
    string Message,
    OptimizationLevel3Preview Preview,
    OptimizationLevel3Record? Record);

public sealed record OptimizationLevel3RestoreResult(
    string RecordId,
    string State,
    string Message,
    int RestoredCount,
    int SkippedCount,
    OptimizationLevel3Record Record);

public sealed record OptimizationLevel3Record(
    string Id,
    string ReportId,
    string TargetKey,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string State,
    IReadOnlyList<OptimizationLevel3AppliedAction> Actions);

public sealed record OptimizationLevel3AppliedAction(
    string Id,
    string Kind,
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    DateTimeOffset? ProcessStartedAt,
    string PreviousDisplayValue,
    string AppliedDisplayValue,
    string PreviousRawValue,
    string AppliedRawValue,
    DateTimeOffset AppliedAt,
    DateTimeOffset? RestoredAt,
    string State,
    string? Message);
