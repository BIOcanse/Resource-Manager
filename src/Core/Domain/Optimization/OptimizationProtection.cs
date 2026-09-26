namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationProtectionStates
{
    public const string Active = "Active";
    public const string Missing = "Missing";
    public const string Stale = "Stale";
}

public static class OptimizationProtectionLevels
{
    public const int None = 0;
    public const int Level1NoFreeze = 1;
    public const int Level2NoOptimization = 2;
    public const int Default = Level2NoOptimization;

    public static int Normalize(int value)
    {
        return value switch
        {
            Level1NoFreeze => Level1NoFreeze,
            Level2NoOptimization => Level2NoOptimization,
            _ => Default
        };
    }
}

public sealed record ProtectedOptimizationTarget(
    string Id,
    string TargetType,
    string TargetKey,
    string DisplayName,
    string? SoftwareId,
    string? SoftwareName,
    string? SoftwareKind,
    DateTimeOffset ProtectedAt,
    string ProtectedReason,
    string CreatedFromReportId,
    DateTimeOffset? LastVerifiedAt,
    string State,
    bool AllowsPlacementAvoidance,
    int ProtectionLevel = OptimizationProtectionLevels.Default);

public sealed record OptimizationProtectionLevelRequest(
    int ProtectionLevel);
