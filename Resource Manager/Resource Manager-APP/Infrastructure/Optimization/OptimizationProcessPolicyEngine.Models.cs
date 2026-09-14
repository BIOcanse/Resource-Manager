using ResourceManager.App.Domain.Optimization;
namespace ResourceManager.App.Infrastructure.Optimization;
internal sealed record ProcessPolicyOptimizationProfile(
    string DisplayName,
    string RecordPrefix,
    string PriorityActionKind,
    string TargetPriorityClass,
    string PriorityDisplayValue,
    string PriorityDetail,
    string PowerThrottlingActionKind,
    string MemoryPriorityActionKind,
    uint TargetMemoryPriority,
    string MemoryPriorityDisplayValue,
    bool IncludeAffinity,
    string? AffinityActionKind);

internal static class ProcessPolicyOptimizationRecordStates
{
    public const string Active = "Active";
    public const string Restored = "Restored";
    public const string PartiallyRestored = "PartiallyRestored";
    public const string NoChanges = "NoChanges";
}

internal sealed record ProcessPolicyOptimizationPreview(
    string ReportId,
    DateTimeOffset CapturedAt,
    bool Eligible,
    string Summary,
    string? DisabledReason,
    OptimizationReportTarget Target,
    CpuAffinityPlan AffinityPlan,
    IReadOnlyList<ProcessPolicyOptimizationActionPreview> Actions);

internal sealed record ProcessPolicyOptimizationActionPreview(
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

internal sealed record ProcessPolicyOptimizationAppliedAction(
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
