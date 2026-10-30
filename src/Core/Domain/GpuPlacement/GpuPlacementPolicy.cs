using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Domain.GpuPlacement;

public static class GpuPlacementPolicyDocumentVersions
{
    public const string Current = "2.0.0";
}

public static class GpuPlacementProviderIds
{
    public const string WindowsGraphicsPreference = "windows-graphics-preference";
    public const string VendorProfile = "vendor-profile";
    public const string DxgiLaunchShim = "dxgi-launch-shim";
    public const string D3dDeviceCreateShim = "d3d-device-create-shim";
    public const string VulkanImplicitLayer = "vulkan-implicit-layer";
    public const string VulkanExplicitLayer = "vulkan-explicit-layer";
    public const string ComputeVisibility = "compute-visibility";

    public static readonly IReadOnlyList<string> Defaults =
    [
        WindowsGraphicsPreference,
        VendorProfile,
        DxgiLaunchShim,
        D3dDeviceCreateShim
    ];

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        WindowsGraphicsPreference,
        VendorProfile,
        DxgiLaunchShim,
        D3dDeviceCreateShim,
        VulkanImplicitLayer,
        VulkanExplicitLayer,
        ComputeVisibility
    };
}

public static class GpuPlacementTargets
{
    public const string SystemDefaultGpu = "system_default_gpu";
    public const string AutoIdleGpu = "auto_idle_gpu";
    public const string IntegratedGpu = "integrated_gpu";
    public const string HighPerformanceGpu = "high_performance_gpu";

    public static bool IsKnown(string? value)
        => value is SystemDefaultGpu or AutoIdleGpu or IntegratedGpu or HighPerformanceGpu
            || IsCanonicalGpuIndexTarget(value);

    public static string Normalize(string? value, string fallback = SystemDefaultGpu)
    {
        var clean = value?.Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return fallback;
        }

        return IsKnown(clean) ? clean : fallback;
    }

    // Compatibility read for previously persisted policy and launch-report targets only.
    // Remove after supported policy files and launch reports have been rewritten or retired.
    public static string ReadStored(string? value)
    {
        var token = value?.Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (var target in new[] { SystemDefaultGpu, AutoIdleGpu, IntegratedGpu, HighPerformanceGpu })
        {
            if (string.Equals(token, target.Replace("_", string.Empty, StringComparison.Ordinal),
                    StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }
        }

        if (IsExactGpuIndexTarget(token))
        {
            return token!.ToLowerInvariant();
        }

        throw new InvalidDataException($"Unsupported stored GPU placement target '{value}'.");
    }

    public static string NormalizeSystemTarget(string? value, string fallback = SystemDefaultGpu)
    {
        var normalized = Normalize(value, fallback);
        if (IsExactGpuIndexTarget(normalized) || normalized.Equals(AutoIdleGpu, StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return normalized;
    }

    public static bool IsExactGpuIndexTarget(string? value)
    {
        var clean = value?.Trim();
        if (string.IsNullOrWhiteSpace(clean) || !clean.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(clean[3..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private static bool IsCanonicalGpuIndexTarget(string? value)
    {
        var clean = value?.Trim();
        return !string.IsNullOrWhiteSpace(clean)
            && clean.StartsWith("gpu", StringComparison.Ordinal)
            && int.TryParse(clean[3..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _);
    }
}

public static class GpuPlacementRuntimeSwitchMethods
{
    public const string FutureFrameTakeover = "future_frame_takeover";
    public const string WindowRerender = "window_rerender";

    public static bool IsKnown(string? value)
        => value is FutureFrameTakeover or WindowRerender;

    public static string Normalize(string? value, string fallback = FutureFrameTakeover)
    {
        return value?.Trim() switch
        {
            FutureFrameTakeover => FutureFrameTakeover,
            WindowRerender => WindowRerender,
            _ => fallback
        };
    }

    // Compatibility read for previously persisted policy values only. Remove after supported
    // policy files have been rewritten or retired.
    public static string ReadStored(string? value)
    {
        var token = value?.Replace("_", string.Empty, StringComparison.Ordinal);
        if (string.Equals(token, FutureFrameTakeover.Replace("_", string.Empty, StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase))
        {
            return FutureFrameTakeover;
        }
        if (string.Equals(token, WindowRerender.Replace("_", string.Empty, StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase))
        {
            return WindowRerender;
        }

        throw new InvalidDataException($"Unsupported stored GPU runtime switch method '{value}'.");
    }
}

public static class CpuMaximumOccupancyModes
{
    public const string SingleCcd = "single_ccd";
    public const string AllCores = "all_cores";

    public static bool IsKnown(string? value)
        => value is SingleCcd or AllCores;

    public static string Normalize(string? value, string fallback = AllCores)
    {
        return value?.Trim() switch
        {
            SingleCcd => SingleCcd,
            AllCores => AllCores,
            _ => fallback
        };
    }

    // Compatibility read for previously persisted policy values only. Remove after supported
    // policy files have been rewritten or retired.
    public static string ReadStored(string? value)
    {
        var token = value?.Replace("_", string.Empty, StringComparison.Ordinal);
        if (string.Equals(token, SingleCcd.Replace("_", string.Empty, StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase))
        {
            return SingleCcd;
        }
        if (string.Equals(token, AllCores.Replace("_", string.Empty, StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase))
        {
            return AllCores;
        }

        throw new InvalidDataException($"Unsupported stored CPU maximum occupancy mode '{value}'.");
    }
}

public sealed record GpuPlacementPolicyDocument(
    string Version,
    IReadOnlyList<GpuPlacementSoftwarePolicy> SoftwarePolicies,
    IReadOnlyList<GpuPlacementProcessPolicy> ProcessPolicies,
    DateTimeOffset UpdatedAt)
{
    public static GpuPlacementPolicyDocument Empty { get; } = new(
        GpuPlacementPolicyDocumentVersions.Current,
        [],
        [],
        DateTimeOffset.MinValue);
}

public sealed record GpuPlacementSoftwarePolicy(
    string SoftwareId,
    string SoftwareName,
    IReadOnlyList<string> AllowedProviders,
    string StartupTargetGpu,
    bool? RuntimeHotSwitchEnabled,
    string PreferredRuntimeSwitchMethod,
    double? BaseScoreOverride,
    bool ProcessOverrideAllowed,
    DateTimeOffset UpdatedAt,
    bool CpuSchedulingExcluded,
    bool MemorySchedulingExcluded,
    bool GpuSchedulingExcluded,
    string CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.AllCores,
    IReadOnlyList<string>? CpuManualExclusivePositionIds = null,
    IReadOnlyList<string>? CpuManualLockedPositionIds = null,
    bool HostMemoryProtectionNoDump = false,
    bool HostMemoryProtectionNeverTerminate = false);

public sealed record GpuPlacementProcessPolicy(
    string SoftwareId,
    string ProcessKey,
    string ProcessName,
    string? ExecutablePath,
    bool Inherit,
    IReadOnlyList<string> AllowedProviders,
    double? BaseScoreOverride,
    DateTimeOffset UpdatedAt,
    bool StartupInterceptionEnabled = false);

public sealed record GpuPlacementSoftwareSettingsSnapshot(
    string SoftwareId,
    string SoftwareName,
    GpuPlacementSoftwarePolicy SoftwarePolicy,
    IReadOnlyList<GpuPlacementProcessPolicy> ProcessPolicies,
    GpuPlacementSoftwareProcessHistory ProcessHistory,
    IReadOnlyList<GpuLaunchInterceptionStatus>? StartupInterceptions = null,
    GpuPlacementTargetInventory? TargetInventory = null,
    IReadOnlyList<GpuPlacementProcessCapabilities>? ProcessCapabilities = null);

public static class GpuPlacementCapabilityStates
{
    public const string Supported = "supported";
    public const string Unsupported = "unsupported";
    public const string Unknown = "unknown";
}

public static class GpuPlacementTargetInventoryStates
{
    public const string Current = "current";
    public const string Unavailable = "unavailable";
}

public sealed record GpuPlacementProviderCapability(
    string State,
    string ProviderId,
    string GraphicsApi,
    string? Architecture,
    string ReasonCode,
    IReadOnlyList<string> MissingFiles);

public sealed record GpuPlacementProcessCapabilities(
    string ProcessKey,
    GpuPlacementProviderCapability Startup,
    GpuPlacementProviderCapability Runtime);

public sealed record GpuPlacementExactTargetOption(
    string TargetGpu,
    string? AdapterName,
    bool Available,
    string? ReasonCode = null);

public sealed record GpuPlacementTargetInventory(
    string State,
    IReadOnlyList<GpuPlacementExactTargetOption> ExactTargets,
    string? ReasonCode = null);

public sealed record GpuPlacementProcessHistoryDocument(
    string Version,
    IReadOnlyList<GpuPlacementSoftwareProcessHistory> SoftwareHistories,
    DateTimeOffset UpdatedAt)
{
    public static GpuPlacementProcessHistoryDocument Empty { get; } = new(
        GpuPlacementPolicyDocumentVersions.Current,
        [],
        DateTimeOffset.MinValue);
}

public sealed record GpuPlacementSoftwareProcessHistory(
    string SoftwareId,
    string SoftwareName,
    IReadOnlyList<GpuPlacementObservedProcess> Processes,
    DateTimeOffset UpdatedAt)
{
    public static GpuPlacementSoftwareProcessHistory Empty(string softwareId, string softwareName)
    {
        return new GpuPlacementSoftwareProcessHistory(softwareId, softwareName, [], DateTimeOffset.MinValue);
    }
}

public sealed record GpuPlacementObservedProcess(
    string ProcessKey,
    string ProcessName,
    string? ExecutablePath,
    string? Architecture,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    int ObservationCount,
    int? LastProcessId,
    IReadOnlyList<string> EvidenceSources,
    byte Confidence,
    GpuGraphicsApi? GraphicsApi = null)
{
    public DateTimeOffset? NoPresentUntil { get; init; }
}

public sealed record GpuPlacementProcessObservationRequest(
    string SoftwareId,
    string SoftwareName,
    IReadOnlyList<GpuPlacementObservedProcessInput> Processes);

public sealed record GpuPlacementObservedProcessInput(
    string ProcessName,
    string? ExecutablePath,
    string? Architecture,
    int? ProcessId,
    IReadOnlyList<string> EvidenceSources);

public static class GpuPlacementPolicyDefaults
{
    public static GpuPlacementSoftwarePolicy CreateSoftwarePolicy(
        string softwareId,
        string softwareName,
        string? softwareKind = null,
        DateTimeOffset? updatedAt = null)
    {
        return new GpuPlacementSoftwarePolicy(
            softwareId,
            softwareName,
            GpuPlacementProviderIds.Defaults,
            GpuPlacementTargets.SystemDefaultGpu,
            ResolveDefaultRuntimeHotSwitchEnabled(softwareKind),
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover,
            null,
            true,
            updatedAt ?? DateTimeOffset.MinValue,
            CpuSchedulingExcluded: false,
            MemorySchedulingExcluded: false,
            GpuSchedulingExcluded: false,
            CpuMaximumOccupancyModes.AllCores,
            [],
            [],
            HostMemoryProtectionNoDump: false);
    }

    public static GpuPlacementProcessPolicy CreateProcessPolicy(
        string softwareId,
        GpuPlacementObservedProcess process,
        DateTimeOffset? updatedAt = null)
    {
        return new GpuPlacementProcessPolicy(
            softwareId,
            process.ProcessKey,
            process.ProcessName,
            process.ExecutablePath,
            true,
            GpuPlacementProviderIds.Defaults,
            null,
            updatedAt ?? DateTimeOffset.MinValue);
    }

    public static bool ResolveDefaultRuntimeHotSwitchEnabled(string? softwareKind)
    {
        return !string.Equals(softwareKind, SoftwareKinds.Game, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(softwareKind, SoftwareKinds.HighPerformance, StringComparison.OrdinalIgnoreCase);
    }
}
