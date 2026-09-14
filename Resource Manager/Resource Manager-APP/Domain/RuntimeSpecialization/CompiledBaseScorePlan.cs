using ResourceManager.App.Domain.Optimization.Scoring;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledBaseScorePlan(
    IReadOnlyDictionary<string, double> SoftwareBaseScoresBySoftwareId,
    IReadOnlyDictionary<string, double> ProcessBaseScoresBySoftwareAndProcessKey)
{
    public static CompiledBaseScorePlan Empty { get; } = new(
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

    public double ResolveBaseScore(
        string? softwareId,
        string? softwareKind,
        string? processKey)
    {
        var defaultBaseScore = OptimizationRuntimeScoringDefaults.BaseScoreForKind(softwareKind ?? string.Empty);
        if (string.IsNullOrWhiteSpace(softwareId))
        {
            return defaultBaseScore;
        }

        var softwareBaseScore = SoftwareBaseScoresBySoftwareId.TryGetValue(softwareId, out var softwareScore)
            ? softwareScore
            : defaultBaseScore;
        if (string.IsNullOrWhiteSpace(processKey))
        {
            return softwareBaseScore;
        }

        return ProcessBaseScoresBySoftwareAndProcessKey.TryGetValue(CreateProcessPolicyKey(softwareId, processKey), out var processScore)
            ? processScore
            : softwareBaseScore;
    }

    public static string CreateProcessPolicyKey(string softwareId, string processKey)
    {
        return $"{softwareId}\n{processKey}";
    }
}
