namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationA1ActionKinds
{
    public const string RaiseProcessPriority = "RaiseProcessPriority";
    public const string DisableExecutionSpeedThrottling = "DisableExecutionSpeedThrottling";
    public const string PreferHighPerformanceGpu = "PreferHighPerformanceGpu";
}

public static class OptimizationA1RecordStates
{
    public const string Active = "Active";
    public const string Restored = "Restored";
    public const string PartiallyRestored = "PartiallyRestored";
    public const string NoChanges = "NoChanges";
}

public sealed record OptimizationA1Request(
    string ReportId,
    bool ConfirmOperation);

public sealed record OptimizationA1RestoreRequest(
    string RecordId,
    bool ConfirmOperation);

public sealed record OptimizationA1Preview(
    string ReportId,
    DateTimeOffset CapturedAt,
    bool Eligible,
    string Summary,
    string? DisabledReason,
    OptimizationReportTarget Target,
    IReadOnlyList<OptimizationA1ActionPreview> Actions);

public sealed record OptimizationA1ActionPreview(
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

public sealed record OptimizationA1ApplyResult(
    string ReportId,
    string State,
    string Message,
    OptimizationA1Preview Preview,
    OptimizationA1Record? Record);

public sealed record OptimizationA1RestoreResult(
    string RecordId,
    string State,
    string Message,
    int RestoredCount,
    int SkippedCount,
    OptimizationA1Record Record);

public sealed record OptimizationA1Record(
    string Id,
    string ReportId,
    string TargetKey,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string State,
    IReadOnlyList<OptimizationA1AppliedAction> Actions);

public sealed record OptimizationA1AppliedAction(
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

public sealed record ProcessPowerThrottlingSnapshot(
    int ProcessId,
    uint Version,
    uint ControlMask,
    uint StateMask,
    string RawValue,
    string DisplayValue);
