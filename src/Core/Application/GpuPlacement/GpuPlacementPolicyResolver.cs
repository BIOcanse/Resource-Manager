using ResourceManager.App.Application.Optimization.Scoring;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

public sealed class GpuPlacementPolicyResolver(GpuPlacementPolicyDocument document)
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

    public ResolvedGpuPlacementPolicy Resolve(
        string softwareId,
        string softwareName,
        string? softwareKind,
        string processName,
        string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(softwareId))
        {
            return ResolvedGpuPlacementPolicy.FromSoftware(
                GpuPlacementPolicyDefaults.CreateSoftwarePolicy(string.Empty, string.Empty, softwareKind),
                softwareKind);
        }

        if (!softwarePolicies.TryGetValue(softwareId, out var softwarePolicy))
        {
            softwarePolicy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy(softwareId, softwareName, softwareKind);
        }

        if (!softwarePolicy.ProcessOverrideAllowed)
        {
            return ResolvedGpuPlacementPolicy.FromSoftware(softwarePolicy, softwareKind);
        }

        var processKey = OptimizationBaseScorePolicyResolver.CreateProcessKey(processName, executablePath);
        return processPolicies.TryGetValue($"{softwareId}\n{processKey}", out var processPolicy)
            && !processPolicy.Inherit
            ? ResolvedGpuPlacementPolicy.FromProcess(processPolicy, softwarePolicy, softwareKind)
            : ResolvedGpuPlacementPolicy.FromSoftware(softwarePolicy, softwareKind);
    }
}
