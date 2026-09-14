namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationProtectionPlacementActionKinds
{
    public const string MoveProtectedAffinity = "MoveProtectedAffinity";
}

public static class OptimizationProtectionPlacementRecordStates
{
    public const string Active = "Active";
    public const string Restored = "Restored";
    public const string PartiallyRestored = "PartiallyRestored";
    public const string NoChanges = "NoChanges";
}

public sealed record OptimizationProtectionPlacementRequest(
    bool ConfirmOperation);

public sealed record OptimizationProtectionPlacementRestoreRequest(
    string RecordId,
    bool ConfirmOperation);

public sealed record OptimizationProtectionPlacementOwner(
    string RecordId,
    string TargetKey,
    string DisplayName,
    long AffinityMask,
    string AffinityDisplay,
    IReadOnlyList<int> LogicalProcessorIds,
    int ActionCount);

public sealed record OptimizationProtectionPlacementPreview(
    DateTimeOffset CapturedAt,
    bool Eligible,
    string Summary,
    string? DisabledReason,
    CpuAffinityPlan AvoidancePlan,
    IReadOnlyList<OptimizationProtectionPlacementOwner> EnhancedOwners,
    IReadOnlyList<OptimizationProtectionPlacementActionPreview> Actions);

public sealed record OptimizationProtectionPlacementActionPreview(
    string Id,
    string Kind,
    string ProtectedTargetId,
    string ProtectedTargetKey,
    string ProtectedTargetName,
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    DateTimeOffset? ProcessStartedAt,
    string CurrentValue,
    string ProposedValue,
    string CurrentRawValue,
    string ProposedRawValue,
    bool WillChange,
    bool Enabled,
    string? DisabledReason,
    IReadOnlyList<string> Details);

public sealed record OptimizationProtectionPlacementApplyResult(
    string State,
    string Message,
    OptimizationProtectionPlacementPreview Preview,
    OptimizationProtectionPlacementRecord? Record);

public sealed record OptimizationProtectionPlacementRestoreResult(
    string RecordId,
    string State,
    string Message,
    int RestoredCount,
    int SkippedCount,
    OptimizationProtectionPlacementRecord Record);

public sealed record OptimizationProtectionPlacementRecord(
    string Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string State,
    long EnhancedAffinityMask,
    long AvoidanceAffinityMask,
    string EnhancedAffinityDisplay,
    string AvoidanceAffinityDisplay,
    IReadOnlyList<OptimizationProtectionPlacementOwner> EnhancedOwners,
    IReadOnlyList<OptimizationProtectionPlacementAppliedAction> Actions);

public sealed record OptimizationProtectionPlacementAppliedAction(
    string Id,
    string Kind,
    string ProtectedTargetId,
    string ProtectedTargetKey,
    string ProtectedTargetName,
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
