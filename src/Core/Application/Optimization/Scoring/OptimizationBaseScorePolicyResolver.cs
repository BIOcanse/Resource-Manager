using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Application.Software;

namespace ResourceManager.App.Application.Optimization.Scoring;

public sealed class OptimizationBaseScorePolicyResolver(GpuPlacementPolicyDocument document, SoftwarePolicyProfile? catalog = null)
{
    private readonly CompiledBaseScorePlan plan = Compile(document, catalog);

    public double ResolveBaseScore(string softwareId, string softwareKind, string processName,
        string? executablePath, IReadOnlyList<string>? serviceNames = null)
        => plan.ResolveBaseScore(softwareId, softwareKind, CreateProcessKey(processName, executablePath),
            processName, executablePath, serviceNames);

    public static CompiledBaseScorePlan Compile(GpuPlacementPolicyDocument gpuPolicyDocument, SoftwarePolicyProfile? catalog = null)
    {
        var softwarePolicies = (gpuPolicyDocument.SoftwarePolicies ?? [])
            .Where(static policy => !string.IsNullOrWhiteSpace(policy.SoftwareId))
            .GroupBy(static policy => policy.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(policy => policy.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var softwareScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in softwarePolicies.Values)
        {
            var score = SanitizeBaseScore(policy.BaseScoreOverride);
            if (score.HasValue)
            {
                softwareScores[policy.SoftwareId] = score.Value;
            }
        }

        var processScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var processPolicy in (gpuPolicyDocument.ProcessPolicies ?? [])
            .GroupBy(static policy => CompiledBaseScorePlan.CreateProcessPolicyKey(policy.SoftwareId, policy.ProcessKey), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(policy => policy.UpdatedAt).First()))
        {
            if (string.IsNullOrWhiteSpace(processPolicy.SoftwareId)
                || string.IsNullOrWhiteSpace(processPolicy.ProcessKey)
                || processPolicy.Inherit
                || !softwarePolicies.TryGetValue(processPolicy.SoftwareId, out var softwarePolicy)
                || !softwarePolicy.ProcessOverrideAllowed)
            {
                continue;
            }

            var score = SanitizeBaseScore(processPolicy.BaseScoreOverride);
            if (score.HasValue)
            {
                processScores[CompiledBaseScorePlan.CreateProcessPolicyKey(
                    processPolicy.SoftwareId,
                    processPolicy.ProcessKey)] = score.Value;
            }
        }

        catalog ??= SoftwarePolicyProfile.Empty;
        var groups = new Dictionary<string, BaseScoreLayer>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in catalog.Assignments.Values)
        {
            if (catalog.Groups.TryGetValue(assignment.AssignedGroupId, out var group) && group.BaseScore is { } value)
                groups[assignment.SoftwareId] = new("catalog_group", value, group.GroupId);
        }
        var entries = new List<CompiledCatalogProcessBaseScore>();
        foreach (var entry in catalog.ProcessAssignments)
        {
            var value = entry.BaseScore ?? (entry.AssignedGroupId is { } id
                && catalog.Groups.TryGetValue(id, out var group) ? group.BaseScore : null);
            if (value is { } score) entries.Add(new(entry.Key, entry.ProcessName, entry.MatchExecutablePath, entry.ServiceName, score));
        }
        return new CompiledBaseScorePlan(softwareScores, processScores)
        {
            CatalogSoftwareScores = groups,
            CatalogProcessScores = entries
        };
    }

    private static double? SanitizeBaseScore(double? value)
    {
        return value.HasValue && double.IsFinite(value.Value)
            ? Math.Round(Math.Clamp(value.Value, 0, 100), 2)
            : null;
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
