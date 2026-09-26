namespace ResourceManager.App.Domain.GpuPlacement;

public sealed record ResolvedGpuPlacementPolicy(
    string EnabledMode,
    string MaxRisk,
    IReadOnlyList<string> AllowedProviders,
    string SchedulingMode,
    string StartupTargetGpu,
    string TargetGpu,
    string RuntimeSchedulingMode,
    string ExplicitSelectionMode,
    bool RuntimeHotSwitchEnabled,
    string PreferredRuntimeSwitchMethod,
    bool KeepCpuProcessesOnMainCcd,
    bool GpuExclusive,
    bool AbsolutePerformanceModeEnabled,
    string CpuMaximumOccupancyMode,
    bool CpuExclusiveLocksAffinity,
    IReadOnlyList<string> CpuManualExclusivePositionIds,
    IReadOnlyList<string> CpuManualLockedPositionIds)
{
    public static ResolvedGpuPlacementPolicy FromSoftware(GpuPlacementSoftwarePolicy policy, string? softwareKind = null)
    {
        var cpuMaximumOccupancyMode = CpuMaximumOccupancyModes.Normalize(policy.CpuMaximumOccupancyMode);
        return new ResolvedGpuPlacementPolicy(
            ResolveSoftwareEnabledMode(policy),
            GpuPlacementRiskLevels.Normalize(policy.MaxRisk),
            NormalizeProviders(policy.AllowedProviders),
            GpuPlacementSchedulingModes.Normalize(policy.SchedulingMode),
            GpuPlacementTargets.Normalize(policy.StartupTargetGpu),
            GpuPlacementTargets.Normalize(policy.TargetGpu),
            GpuPlacementRuntimeSchedulingModes.Normalize(policy.RuntimeSchedulingMode),
            GpuPlacementExplicitSelectionModes.Normalize(policy.ExplicitSelectionMode),
            policy.RuntimeHotSwitchEnabled ?? GpuPlacementPolicyDefaults.ResolveDefaultRuntimeHotSwitchEnabled(softwareKind),
            GpuPlacementRuntimeSwitchMethods.Normalize(policy.PreferredRuntimeSwitchMethod),
            cpuMaximumOccupancyMode.Equals(CpuMaximumOccupancyModes.SingleCcd, StringComparison.OrdinalIgnoreCase),
            policy.GpuExclusive,
            policy.AbsolutePerformanceModeEnabled,
            cpuMaximumOccupancyMode,
            policy.CpuExclusiveLocksAffinity,
            NormalizeCpuPositionIds(policy.CpuManualExclusivePositionIds),
            NormalizeCpuPositionIds(policy.CpuManualLockedPositionIds));
    }

    public static ResolvedGpuPlacementPolicy FromProcess(
        GpuPlacementProcessPolicy policy,
        GpuPlacementSoftwarePolicy softwarePolicy,
        string? softwareKind = null)
    {
        var cpuMaximumOccupancyMode = CpuMaximumOccupancyModes.Normalize(softwarePolicy.CpuMaximumOccupancyMode);
        var softwareEnabledMode = ResolveSoftwareEnabledMode(softwarePolicy);
        var processEnabledMode = GpuPlacementPolicyModes.Normalize(policy.EnabledMode, softwareEnabledMode);
        return new ResolvedGpuPlacementPolicy(
            processEnabledMode.Equals(GpuPlacementPolicyModes.Inherit, StringComparison.OrdinalIgnoreCase)
                ? softwareEnabledMode
                : processEnabledMode,
            GpuPlacementRiskLevels.Normalize(policy.MaxRisk),
            NormalizeProviders(policy.AllowedProviders),
            GpuPlacementSchedulingModes.Normalize(softwarePolicy.SchedulingMode),
            GpuPlacementTargets.Normalize(softwarePolicy.StartupTargetGpu),
            GpuPlacementTargets.Normalize(policy.TargetGpu),
            GpuPlacementRuntimeSchedulingModes.Normalize(softwarePolicy.RuntimeSchedulingMode),
            GpuPlacementExplicitSelectionModes.Normalize(policy.ExplicitSelectionMode),
            softwarePolicy.RuntimeHotSwitchEnabled ?? GpuPlacementPolicyDefaults.ResolveDefaultRuntimeHotSwitchEnabled(softwareKind),
            GpuPlacementRuntimeSwitchMethods.Normalize(softwarePolicy.PreferredRuntimeSwitchMethod),
            cpuMaximumOccupancyMode.Equals(CpuMaximumOccupancyModes.SingleCcd, StringComparison.OrdinalIgnoreCase),
            softwarePolicy.GpuExclusive,
            softwarePolicy.AbsolutePerformanceModeEnabled,
            cpuMaximumOccupancyMode,
            softwarePolicy.CpuExclusiveLocksAffinity,
            NormalizeCpuPositionIds(softwarePolicy.CpuManualExclusivePositionIds),
            NormalizeCpuPositionIds(softwarePolicy.CpuManualLockedPositionIds));
    }

    public bool AllowsProvider(string providerId)
    {
        return AllowedProviders.Any(provider => provider.Equals(providerId, StringComparison.OrdinalIgnoreCase));
    }

    public bool AllowsRuntimeShimExecution()
    {
        return SchedulingMode.Equals(GpuPlacementSchedulingModes.Precise, StringComparison.OrdinalIgnoreCase)
            && RuntimeSchedulingMode.Equals(GpuPlacementRuntimeSchedulingModes.Precise, StringComparison.OrdinalIgnoreCase)
            && (EnabledMode.Equals(GpuPlacementPolicyModes.Auto, StringComparison.OrdinalIgnoreCase)
                || EnabledMode.Equals(GpuPlacementPolicyModes.Manual, StringComparison.OrdinalIgnoreCase))
            && (AllowsProvider(GpuPlacementProviderIds.D3dDeviceCreateShim)
                || AllowsProvider(GpuPlacementProviderIds.VulkanExplicitLayer));
    }

    public bool AcceptsRuntimeGpuScheduling()
    {
        return RuntimeHotSwitchEnabled && AllowsRuntimeShimExecution();
    }

    public bool AcceptsExternalRuntimeGpuScheduling()
    {
        return RuntimeHotSwitchEnabled
            && SchedulingMode.Equals(GpuPlacementSchedulingModes.Precise, StringComparison.OrdinalIgnoreCase)
            && RuntimeSchedulingMode.Equals(GpuPlacementRuntimeSchedulingModes.Precise, StringComparison.OrdinalIgnoreCase)
            && (EnabledMode.Equals(GpuPlacementPolicyModes.Auto, StringComparison.OrdinalIgnoreCase)
                || EnabledMode.Equals(GpuPlacementPolicyModes.Manual, StringComparison.OrdinalIgnoreCase));
    }

    public string? GetRuntimeProvider(GpuGraphicsApi? graphicsApi)
        => AcceptsRuntimeGpuScheduling() ? GpuGraphicsApiRoutes.RuntimeProvider(graphicsApi) : null;

    public bool AllowsStartupShimExecution()
    {
        return SchedulingMode.Equals(GpuPlacementSchedulingModes.Precise, StringComparison.OrdinalIgnoreCase)
            && (EnabledMode.Equals(GpuPlacementPolicyModes.Auto, StringComparison.OrdinalIgnoreCase)
                || EnabledMode.Equals(GpuPlacementPolicyModes.Manual, StringComparison.OrdinalIgnoreCase))
            && (AllowsProvider(GpuPlacementProviderIds.D3dDeviceCreateShim)
                || AllowsProvider(GpuPlacementProviderIds.VulkanExplicitLayer));
    }

    public IReadOnlyList<string> GetStartupProviders(GpuGraphicsApi? graphicsApi)
    {
        if (!AllowsStartupShimExecution()) return [];
        var provider = GpuGraphicsApiRoutes.StartupProvider(graphicsApi);
        return provider is null ? [] : [provider];
    }

    private static string ResolveSoftwareEnabledMode(GpuPlacementSoftwarePolicy policy)
    {
        var enabledMode = GpuPlacementPolicyModes.Normalize(policy.EnabledMode);
        return enabledMode.Equals(GpuPlacementPolicyModes.Inherit, StringComparison.OrdinalIgnoreCase)
            ? GpuPlacementPolicyModes.Auto
            : enabledMode;
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
