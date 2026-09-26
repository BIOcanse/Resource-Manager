using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization.Scoring;

namespace ResourceManager.App.Application.Optimization.Scoring;

public sealed class OptimizationBaseScorePolicyResolver(GpuPlacementPolicyDocument document)
{
    private readonly IReadOnlyDictionary<string, GpuPlacementSoftwarePolicy> softwarePolicies =
        (document.SoftwarePolicies ?? [])
        .Where(static policy => !string.IsNullOrWhiteSpace(policy.SoftwareId))
        .GroupBy(static policy => policy.SoftwareId, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            static group => group.Key,
            static group => group.OrderByDescending(policy => policy.UpdatedAt).First(),
            StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyDictionary<string, GpuPlacementProcessPolicy> processPolicies =
        (document.ProcessPolicies ?? [])
        .Where(static policy => !string.IsNullOrWhiteSpace(policy.SoftwareId)
            && !string.IsNullOrWhiteSpace(policy.ProcessKey))
        .GroupBy(static policy => $"{policy.SoftwareId}\n{policy.ProcessKey}", StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            static group => group.Key,
            static group => group.OrderByDescending(policy => policy.UpdatedAt).First(),
            StringComparer.OrdinalIgnoreCase);

    public double ResolveBaseScore(
        string softwareId,
        string softwareKind,
        string processName,
        string? executablePath)
    {
        var defaultBaseScore = OptimizationRuntimeScoringDefaults.BaseScoreForKind(softwareKind);
        if (string.IsNullOrWhiteSpace(softwareId))
        {
            return defaultBaseScore;
        }

        softwarePolicies.TryGetValue(softwareId, out var softwarePolicy);
        var softwareBaseScore = SanitizeBaseScore(softwarePolicy?.BaseScoreOverride) ?? defaultBaseScore;
        if (softwarePolicy is null || !softwarePolicy.ProcessOverrideAllowed)
        {
            return softwareBaseScore;
        }

        var processKey = CreateProcessKey(processName, executablePath);
        return processPolicies.TryGetValue($"{softwareId}\n{processKey}", out var processPolicy)
            && !processPolicy.Inherit
            ? SanitizeBaseScore(processPolicy.BaseScoreOverride) ?? softwareBaseScore
            : softwareBaseScore;
    }

    public static string CreateProcessKey(
        string? processName,
        string? executablePath)
    {
        var path = CleanPath(executablePath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return $"path:{ShortHash(path)}";
        }

        var name = CleanText(processName) ?? "unknown";
        return $"name:{ShortHash(name)}";
    }

    private static double? SanitizeBaseScore(double? value)
    {
        if (value is null || !double.IsFinite(value.Value))
        {
            return null;
        }

        return Math.Round(Math.Clamp(value.Value, 0, 100), 2);
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static string? CleanText(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    private static string? CleanPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value.Trim();
        }
    }
}
