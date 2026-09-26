using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Domain.Optimization.Scoring;

public static class OptimizationRuntimeScoringDefaults
{
    public const double HighTierMinimumBaseScore = 81;
    public const double MiddleTierMinimumBaseScore = 21;

    public static double BaseScoreForKind(string kind)
    {
        return kind switch
        {
            SoftwareKinds.Game => 95,
            SoftwareKinds.HighPerformance => 85,
            SoftwareKinds.WindowsSystem or SoftwareKinds.WindowsComponent or SoftwareKinds.WindowsService => 90,
            SoftwareKinds.Adapted => 80,
            SoftwareKinds.RuntimeProduct or SoftwareKinds.RuntimePackage or SoftwareKinds.RuntimeRoot => 55,
            SoftwareKinds.Controlled => 45,
            SoftwareKinds.Managed => 42,
            SoftwareKinds.Unattributed => 30,
            _ => 35
        };
    }

    public static HostManagerBaseScoreTier ResolveBaseScoreTier(double baseScore)
        => baseScore >= HighTierMinimumBaseScore
            ? HostManagerBaseScoreTier.High
            : baseScore >= MiddleTierMinimumBaseScore
                ? HostManagerBaseScoreTier.Middle
                : HostManagerBaseScoreTier.Low;
}

public enum HostManagerBaseScoreTier : byte
{
    Low = 0,
    Middle = 1,
    High = 2
}
