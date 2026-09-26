namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationA2ActionKinds
{
    public const string RaiseProcessPriority = "RaiseProcessPriority";
    public const string DisableExecutionSpeedThrottling = "DisableExecutionSpeedThrottling";
    public const string PreferHighPerformanceGpu = "PreferHighPerformanceGpu";
    public const string MoveTargetAffinity = "MoveTargetAffinity";
}

public static class OptimizationA2RecordStates
{
    public const string Active = "Active";
    public const string Restored = "Restored";
    public const string PartiallyRestored = "PartiallyRestored";
    public const string NoChanges = "NoChanges";
}

public sealed record OptimizationA2Request(
    string ReportId,
    bool ConfirmOperation);

public sealed record OptimizationA2RestoreRequest(
    string RecordId,
    bool ConfirmOperation);

public sealed record OptimizationA2Preview(
    string ReportId,
    DateTimeOffset CapturedAt,
    bool Eligible,
    string Summary,
    string? DisabledReason,
    OptimizationReportTarget Target,
    CpuAffinityPlan TargetAffinityPlan,
    IReadOnlyList<OptimizationA2ActionPreview> Actions);

public sealed record OptimizationA2ActionPreview(
    string Id,
    string Kind,
    int? ProcessId,
    string? ProcessName,
    string? ExecutablePath,
    DateTimeOffset? ProcessStartedAt,
    IReadOnlyList<int> ProcessIds,
    IReadOnlyList<string> ProcessNames,
    string CurrentValue,
    string ProposedValue,
    string? CurrentRawValue,
    string ProposedRawValue,
    bool WillChange,
    bool Enabled,
    string? DisabledReason,
    IReadOnlyList<string> Details);

public sealed record OptimizationA2ApplyResult(
    string ReportId,
    string State,
    string Message,
    OptimizationA2Preview Preview,
    OptimizationA2Record? Record);

public sealed record OptimizationA2RestoreResult(
    string RecordId,
    string State,
    string Message,
    int RestoredCount,
    int SkippedCount,
    OptimizationA2Record Record);

public sealed record OptimizationA2Record(
    string Id,
    string ReportId,
    string TargetKey,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string State,
    IReadOnlyList<OptimizationA2AppliedAction> Actions);

public sealed record OptimizationA2AppliedAction(
    string Id,
    string Kind,
    int? ProcessId,
    string? ProcessName,
    string? ExecutablePath,
    DateTimeOffset? ProcessStartedAt,
    IReadOnlyList<int> ProcessIds,
    IReadOnlyList<string> ProcessNames,
    string PreviousDisplayValue,
    string AppliedDisplayValue,
    string? PreviousRawValue,
    string AppliedRawValue,
    DateTimeOffset AppliedAt,
    DateTimeOffset? RestoredAt,
    string State,
    string? Message);
