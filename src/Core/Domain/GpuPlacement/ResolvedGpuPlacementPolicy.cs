namespace ResourceManager.App.Domain.GpuPlacement;

public sealed record ResolvedGpuPlacementPolicy(
    IReadOnlyList<string> AllowedProviders,
    string StartupTargetGpu,
    bool RuntimeHotSwitchEnabled,
    string PreferredRuntimeSwitchMethod,
    string CpuMaximumOccupancyMode,
    IReadOnlyList<string> CpuManualExclusivePositionIds,
    IReadOnlyList<string> CpuManualLockedPositionIds,
    bool CpuSchedulingExcluded,
    bool MemorySchedulingExcluded,
    bool GpuSchedulingExcluded)
{
    public static ResolvedGpuPlacementPolicy FromSoftware(GpuPlacementSoftwarePolicy policy, string? softwareKind = null)
    {
        return new ResolvedGpuPlacementPolicy(
            NormalizeProviders(policy.AllowedProviders),
            GpuPlacementTargets.Normalize(policy.StartupTargetGpu),
            policy.RuntimeHotSwitchEnabled ?? GpuPlacementPolicyDefaults.ResolveDefaultRuntimeHotSwitchEnabled(softwareKind),
            GpuPlacementRuntimeSwitchMethods.Normalize(policy.PreferredRuntimeSwitchMethod),
            CpuMaximumOccupancyModes.Normalize(policy.CpuMaximumOccupancyMode),
            NormalizeCpuPositionIds(policy.CpuManualExclusivePositionIds),
            NormalizeCpuPositionIds(policy.CpuManualLockedPositionIds),
            policy.CpuSchedulingExcluded,
            policy.MemorySchedulingExcluded,
            policy.GpuSchedulingExcluded);
    }

    // A process override only changes its providers (and its base score elsewhere); scheduling
    // participation is a software-level choice and is inherited unchanged.
    public static ResolvedGpuPlacementPolicy FromProcess(
        GpuPlacementProcessPolicy policy,
        GpuPlacementSoftwarePolicy softwarePolicy,
        string? softwareKind = null)
    {
        return FromSoftware(softwarePolicy, softwareKind) with
        {
            AllowedProviders = NormalizeProviders(policy.AllowedProviders)
        };
    }

    public bool AllowsProvider(string providerId)
    {
        return AllowedProviders.Any(provider => provider.Equals(providerId, StringComparison.OrdinalIgnoreCase));
    }

    public bool AllowsRuntimeShimExecution()
    {
        return !GpuSchedulingExcluded
            && (AllowsProvider(GpuPlacementProviderIds.D3dDeviceCreateShim)
                || AllowsProvider(GpuPlacementProviderIds.VulkanExplicitLayer));
    }

    public bool AcceptsRuntimeGpuScheduling()
    {
        return RuntimeHotSwitchEnabled && AllowsRuntimeShimExecution();
    }

    public bool AcceptsExternalRuntimeGpuScheduling()
    {
        return RuntimeHotSwitchEnabled && !GpuSchedulingExcluded;
    }

    public string? GetRuntimeProvider(GpuGraphicsApi? graphicsApi)
        => AcceptsRuntimeGpuScheduling() ? GpuGraphicsApiRoutes.RuntimeProvider(graphicsApi) : null;

    public bool AllowsStartupShimExecution()
    {
        return !GpuSchedulingExcluded
            && (AllowsProvider(GpuPlacementProviderIds.D3dDeviceCreateShim)
                || AllowsProvider(GpuPlacementProviderIds.VulkanExplicitLayer));
    }

    public IReadOnlyList<string> GetStartupProviders(GpuGraphicsApi? graphicsApi)
    {
        if (!AllowsStartupShimExecution()) return [];
        var provider = GpuGraphicsApiRoutes.StartupProvider(graphicsApi);
        return provider is null ? [] : [provider];
    }

    private static IReadOnlyList<string> NormalizeProviders(IReadOnlyList<string>? providers)
    {
        var normalized = (providers ?? GpuPlacementProviderIds.Defaults)
            .Where(static provider => !string.IsNullOrWhiteSpace(provider))
            .Select(static provider => provider.Trim())
            .Where(static provider => GpuPlacementProviderIds.Known.Contains(provider))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static provider => provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized;
    }

    private static IReadOnlyList<string> NormalizeCpuPositionIds(IEnumerable<string>? positionIds)
    {
        return (positionIds ?? [])
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
