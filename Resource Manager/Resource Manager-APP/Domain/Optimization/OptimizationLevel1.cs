namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationLevel1ActionKinds
{
    public const string NormalizeProcessPriority = "NormalizeProcessPriority";
    public const string EnableExecutionSpeedThrottling = "EnableExecutionSpeedThrottling";
    public const string LowerMemoryPriority = "LowerMemoryPriority";
}

public static class OptimizationLevel1RecordStates
{
    public const string Active = "Active";
    public const string Restored = "Restored";
    public const string PartiallyRestored = "PartiallyRestored";
    public const string NoChanges = "NoChanges";
}

public sealed record OptimizationLevel1Request(
    string ReportId,
    bool ConfirmOperation);

public sealed record OptimizationLevel1RestoreRequest(
    string RecordId,
    bool ConfirmOperation);

public sealed record OptimizationLevel1Preview(
    string ReportId,
    DateTimeOffset CapturedAt,
    bool Eligible,
    string Summary,
    string? DisabledReason,
    OptimizationReportTarget Target,
    IReadOnlyList<OptimizationLevel1ActionPreview> Actions);

public sealed record OptimizationLevel1ActionPreview(
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

public sealed record OptimizationLevel1ApplyResult(
    string ReportId,
    string State,
    string Message,
    OptimizationLevel1Preview Preview,
    OptimizationLevel1Record? Record);

public sealed record OptimizationLevel1RestoreResult(
    string RecordId,
    string State,
    string Message,
    int RestoredCount,
    int SkippedCount,
    OptimizationLevel1Record Record);

public sealed record OptimizationLevel1Record(
    string Id,
    string ReportId,
    string TargetKey,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string State,
    IReadOnlyList<OptimizationLevel1AppliedAction> Actions);

public sealed record OptimizationLevel1AppliedAction(
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
