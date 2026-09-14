using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledGpuPlacementPlan(
    bool GlobalPreciseProviderEnabled,
    IReadOnlyDictionary<string, ResolvedGpuPlacementPolicy> SoftwarePoliciesBySoftwareId,
    IReadOnlyDictionary<string, ResolvedGpuPlacementPolicy> ProcessPoliciesBySoftwareAndProcessKey)
{
    public static CompiledGpuPlacementPlan Default { get; } = new(
        false,
        new Dictionary<string, ResolvedGpuPlacementPolicy>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, ResolvedGpuPlacementPolicy>(StringComparer.OrdinalIgnoreCase));

    public ResolvedGpuPlacementPolicy Resolve(
        string? softwareId,
        string? softwareName,
        string? softwareKind,
        string? processKey)
    {
        if (string.IsNullOrWhiteSpace(softwareId))
        {
            return ResolvedGpuPlacementPolicy.FromSoftware(
                GpuPlacementPolicyDefaults.CreateSoftwarePolicy(string.Empty, string.Empty, softwareKind),
                softwareKind);
        }

        if (!string.IsNullOrWhiteSpace(processKey)
            && ProcessPoliciesBySoftwareAndProcessKey.TryGetValue(
                CompiledBaseScorePlan.CreateProcessPolicyKey(softwareId, processKey),
                out var processPolicy))
        {
            return processPolicy;
        }

        return SoftwarePoliciesBySoftwareId.TryGetValue(softwareId, out var softwarePolicy)
            ? softwarePolicy
            : ResolvedGpuPlacementPolicy.FromSoftware(
                GpuPlacementPolicyDefaults.CreateSoftwarePolicy(
                    softwareId,
                    string.IsNullOrWhiteSpace(softwareName) ? softwareId : softwareName,
                    softwareKind),
                softwareKind);
    }
}
