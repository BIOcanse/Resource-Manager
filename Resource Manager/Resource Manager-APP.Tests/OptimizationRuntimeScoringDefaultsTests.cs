using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed class OptimizationRuntimeScoringDefaultsTests
{
    [Fact]
    public void BaseScoreForKind_CoversEverySoftwareKindAndUnknownSoftware()
    {
        var kinds = typeof(SoftwareKinds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(static field => field.IsLiteral && !field.IsInitOnly)
            .Select(static field => Assert.IsType<string>(field.GetRawConstantValue()))
            .ToArray();

        Assert.All(kinds, kind => Assert.True(
            OptimizationRuntimeScoringDefaults.BaseScoreForKind(kind) >=
                OptimizationRuntimeScoringDefaults.MiddleTierMinimumBaseScore,
            $"Software kind '{kind}' must default to the middle or high base-score tier."));
        Assert.Equal(35d, OptimizationRuntimeScoringDefaults.BaseScoreForKind("UnknownFutureKind"));
        Assert.Equal(
            HostManagerBaseScoreTier.Middle,
            OptimizationRuntimeScoringDefaults.ResolveBaseScoreTier(
                OptimizationRuntimeScoringDefaults.BaseScoreForKind("UnknownFutureKind")));
    }

    [Theory]
    [InlineData(20, HostManagerBaseScoreTier.Low)]
    [InlineData(21, HostManagerBaseScoreTier.Middle)]
    [InlineData(80, HostManagerBaseScoreTier.Middle)]
    [InlineData(81, HostManagerBaseScoreTier.High)]
    public void ExplicitBaseScoreResolvesAtExactGlobalTierBoundaries(
        double baseScore,
        HostManagerBaseScoreTier expected)
    {
        Assert.Equal(expected, OptimizationRuntimeScoringDefaults.ResolveBaseScoreTier(baseScore));
    }
}
