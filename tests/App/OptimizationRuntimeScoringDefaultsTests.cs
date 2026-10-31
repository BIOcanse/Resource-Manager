using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed class OptimizationRuntimeScoringDefaultsTests
{
    [Fact]
    public void BaseScoreForKind_ReadsTheDeclaredKindScoresAndOtherForUnknownSoftware()
    {
        var declared = HostManagerTestPlanFactory.CreatePlan().Optimization!.SoftwareKindBaseScores;
        var kinds = typeof(SoftwareKinds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(static field => field.IsLiteral && !field.IsInitOnly)
            .Select(static field => Assert.IsType<string>(field.GetRawConstantValue()))
            .ToArray();

        Assert.All(kinds, kind => Assert.Equal(declared.ForKind(kind), OptimizationRuntimeScoringDefaults.BaseScoreForKind(kind)));
        Assert.Equal(declared.Other, OptimizationRuntimeScoringDefaults.BaseScoreForKind("UnknownFutureKind"));
        // The declared defaults are today's values.
        Assert.Equal(95d, OptimizationRuntimeScoringDefaults.BaseScoreForKind(SoftwareKinds.Game));
        Assert.Equal(35d, OptimizationRuntimeScoringDefaults.BaseScoreForKind("UnknownFutureKind"));
    }
}
