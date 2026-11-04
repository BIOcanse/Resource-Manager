namespace ResourceManager.App.Domain.Optimization.Scoring;

public static class OptimizationRuntimeScoringDefaults
{
    /// <summary>Type-default base score of a software kind, from the declared software_kind_base_scores point.</summary>
    public static double BaseScoreForKind(string kind)
        => ResourceManager.App.Infrastructure.Optimization.OptimizationSettings.ProductDefaults.SoftwareKindBaseScores.ForKind(kind);
}
