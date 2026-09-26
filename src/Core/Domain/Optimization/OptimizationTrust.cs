namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationTrustStates
{
    public const string Active = "Active";
    public const string Missing = "Missing";
    public const string Stale = "Stale";
}

public sealed record TrustedOptimizationTarget(
    string Id,
    string TargetType,
    string TargetKey,
    string DisplayName,
    string ReportType,
    string ResourceKind,
    DateTimeOffset TrustedAt,
    string TrustedReason,
    string CreatedFromReportId,
    DateTimeOffset? LastVerifiedAt,
    string State);
