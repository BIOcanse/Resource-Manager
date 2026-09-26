namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationLevel2ActionKinds
{
    public const string LowerProcessPriority = "LowerProcessPriority";
    public const string EnableExecutionSpeedThrottling = "EnableExecutionSpeedThrottling";
    public const string LowerMemoryPriority = "LowerMemoryPriority";
    public const string MoveProcessAffinity = "MoveProcessAffinity";
}

public static class OptimizationLevel2RecordStates
{
    public const string Active = "Active";
    public const string Restored = "Restored";
    public const string PartiallyRestored = "PartiallyRestored";
    public const string NoChanges = "NoChanges";
}

public sealed record OptimizationLevel2Request(
    string ReportId,
    bool ConfirmOperation);

public sealed record OptimizationLevel2RestoreRequest(
    string RecordId,
    bool ConfirmOperation);

public sealed record OptimizationLevel2Preview(
    string ReportId,
    DateTimeOffset CapturedAt,
    bool Eligible,
    string Summary,
    string? DisabledReason,
    OptimizationReportTarget Target,
    CpuAffinityPlan AffinityPlan,
    IReadOnlyList<OptimizationLevel2ActionPreview> Actions);

public sealed record OptimizationLevel2ActionPreview(
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

public sealed record OptimizationLevel2ApplyResult(
    string ReportId,
    string State,
    string Message,
    OptimizationLevel2Preview Preview,
    OptimizationLevel2Record? Record);

public sealed record OptimizationLevel2RestoreResult(
    string RecordId,
    string State,
    string Message,
    int RestoredCount,
    int SkippedCount,
    OptimizationLevel2Record Record);

public sealed record OptimizationLevel2Record(
    string Id,
    string ReportId,
    string TargetKey,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string State,
    IReadOnlyList<OptimizationLevel2AppliedAction> Actions);

public sealed record OptimizationLevel2AppliedAction(
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

public sealed record ProcessResourcePolicySnapshot(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    DateTimeOffset? StartedAt,
    string PriorityClass,
    long ProcessorAffinityMask,
    int LogicalProcessorCount,
    uint? MemoryPriority = null,
    string? MemoryPriorityRawValue = null,
    string? MemoryPriorityDisplayValue = null,
    uint? PowerThrottlingControlMask = null,
    uint? PowerThrottlingStateMask = null,
    string? PowerThrottlingRawValue = null,
    string? PowerThrottlingDisplayValue = null);

public sealed record ProcessMemoryPrioritySnapshot(
    int ProcessId,
    uint MemoryPriority,
    string RawValue,
    string DisplayValue);

public sealed record ThreadCpuSetPolicySnapshot(
    int ProcessId,
    int ThreadId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<uint> CpuSetIds);

public sealed record ProcessResourcePolicyWriteResult(
    bool Succeeded,
    string Message);
