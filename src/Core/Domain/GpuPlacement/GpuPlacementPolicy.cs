using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Domain.GpuPlacement;

public static class GpuPlacementPolicyDocumentVersions
{
    public const string Current = "1.2.0";
}

public static class GpuPlacementPolicyModes
{
    public const string Inherit = "Inherit";
    public const string Disabled = "Disabled";
    public const string Preview = "Preview";
    public const string Auto = "Auto";
    public const string Manual = "Manual";

    public static string Normalize(string? value, string fallback = Preview)
    {
        return value?.Trim() switch
        {
            Inherit => Inherit,
            Disabled => Disabled,
            Preview => Preview,
            Auto => Auto,
            Manual => Manual,
            _ => fallback
        };
    }
}

public static class GpuPlacementRiskLevels
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";

    public static string Normalize(string? value, string fallback = Low)
    {
        return value?.Trim() switch
        {
            Low => Low,
            Medium => Medium,
            High => High,
            _ => fallback
        };
    }
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
    public const string SystemDefaultGpu = "SystemDefaultGpu";
    public const string AutoIdleGpu = "AutoIdleGpu";
    public const string IntegratedGpu = "IntegratedGpu";
    public const string HighPerformanceGpu = "HighPerformanceGpu";

    public static string Normalize(string? value, string fallback = SystemDefaultGpu)
    {
        var clean = value?.Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return fallback;
        }

        if (clean.Equals(SystemDefaultGpu, StringComparison.OrdinalIgnoreCase))
        {
            return SystemDefaultGpu;
        }

        if (clean.Equals(AutoIdleGpu, StringComparison.OrdinalIgnoreCase))
        {
            return AutoIdleGpu;
        }

        if (clean.Equals(IntegratedGpu, StringComparison.OrdinalIgnoreCase))
        {
            return IntegratedGpu;
        }

        if (clean.Equals(HighPerformanceGpu, StringComparison.OrdinalIgnoreCase))
        {
            return HighPerformanceGpu;
        }

        return IsExactGpuIndexTarget(clean)
            ? clean.ToUpperInvariant()
            : fallback;
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
}

public static class GpuPlacementExplicitSelectionModes
{
    public const string DefaultSkip = "DefaultSkip";
    public const string PreviewOnly = "PreviewOnly";
    public const string AllowLow = "AllowLow";
    public const string AllowMedium = "AllowMedium";
    public const string AllowHigh = "AllowHigh";

    public static string Normalize(string? value, string fallback = DefaultSkip)
    {
        return value?.Trim() switch
        {
            DefaultSkip => DefaultSkip,
            PreviewOnly => PreviewOnly,
            AllowLow => AllowLow,
            AllowMedium => AllowMedium,
            AllowHigh => AllowHigh,
            _ => fallback
        };
    }
}

public static class GpuPlacementRuntimeSwitchMethods
{
    public const string FutureFrameTakeover = "FutureFrameTakeover";
    public const string WindowRerender = "WindowRerender";

    public static string Normalize(string? value, string fallback = FutureFrameTakeover)
    {
        return value?.Trim() switch
        {
            FutureFrameTakeover => FutureFrameTakeover,
            WindowRerender => WindowRerender,
            _ => fallback
        };
    }
}

public static class GpuPlacementSchedulingModes
{
    public const string Precise = "Precise";
    public const string Ordinary = "Ordinary";

    public static string Normalize(string? value, string fallback = Precise)
    {
        return value?.Trim() switch
        {
            Precise => Precise,
            Ordinary => Ordinary,
            _ => fallback
        };
    }
}

public static class GpuPlacementRuntimeSchedulingModes
{
    public const string Precise = "Precise";
    public const string Ordinary = "Ordinary";

    public static string Normalize(string? value, string fallback = Precise)
    {
        return value?.Trim() switch
        {
            Precise => Precise,
            Ordinary => Ordinary,
            _ => fallback
        };
    }
}

public static class CpuMaximumOccupancyModes
{
    public const string SingleCcd = "SingleCcd";
    public const string AllCores = "AllCores";

    public static string Normalize(string? value, string fallback = SingleCcd)
    {
        return value?.Trim() switch
        {
            SingleCcd => SingleCcd,
            AllCores => AllCores,
            _ => fallback
        };
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
    string EnabledMode,
    string MaxRisk,
    IReadOnlyList<string> AllowedProviders,
    string SchedulingMode,
    string StartupTargetGpu,
    string TargetGpu,
    string RuntimeSchedulingMode,
    string ExplicitSelectionMode,
    bool? RuntimeHotSwitchEnabled,
    string PreferredRuntimeSwitchMethod,
    double? BaseScoreOverride,
    bool ProcessOverrideAllowed,
    DateTimeOffset UpdatedAt,
    bool? KeepCpuProcessesOnMainCcd = null,
    bool GpuExclusive = false,
    bool AbsolutePerformanceModeEnabled = false,
    string CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.SingleCcd,
    bool CpuExclusiveLocksAffinity = false,
    IReadOnlyList<string>? CpuManualExclusivePositionIds = null,
    IReadOnlyList<string>? CpuManualLockedPositionIds = null);

public sealed record GpuPlacementProcessPolicy(
    string SoftwareId,
    string ProcessKey,
    string ProcessName,
    string? ExecutablePath,
    bool Inherit,
    string EnabledMode,
    string MaxRisk,
    IReadOnlyList<string> AllowedProviders,
    string TargetGpu,
    string ExplicitSelectionMode,
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
    string Reason);

public sealed record GpuPlacementProcessCapabilities(
    string ProcessKey,
    GpuPlacementProviderCapability Startup,
    GpuPlacementProviderCapability Runtime);

public sealed record GpuPlacementExactTargetOption(
    string TargetGpu,
    string DisplayName,
    bool Available,
    string? Reason = null);

public sealed record GpuPlacementTargetInventory(
    string State,
    IReadOnlyList<GpuPlacementExactTargetOption> ExactTargets,
    string? Reason = null);

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
    GpuGraphicsApi? GraphicsApi = null);

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
            GpuPlacementPolicyModes.Auto,
            GpuPlacementRiskLevels.Low,
            GpuPlacementProviderIds.Defaults,
            GpuPlacementSchedulingModes.Precise,
            GpuPlacementTargets.SystemDefaultGpu,
            GpuPlacementTargets.AutoIdleGpu,
            GpuPlacementRuntimeSchedulingModes.Precise,
            GpuPlacementExplicitSelectionModes.DefaultSkip,
            ResolveDefaultRuntimeHotSwitchEnabled(softwareKind),
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover,
            null,
            true,
            updatedAt ?? DateTimeOffset.MinValue,
            true,
            false,
            false,
            CpuMaximumOccupancyModes.SingleCcd,
            false,
            [],
            []);
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
            GpuPlacementPolicyModes.Inherit,
            GpuPlacementRiskLevels.Low,
            GpuPlacementProviderIds.Defaults,
            GpuPlacementTargets.SystemDefaultGpu,
            GpuPlacementExplicitSelectionModes.DefaultSkip,
            null,
            updatedAt ?? DateTimeOffset.MinValue);
    }

    public static bool ResolveDefaultRuntimeHotSwitchEnabled(string? softwareKind)
    {
        return !string.Equals(softwareKind, SoftwareKinds.Game, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(softwareKind, SoftwareKinds.HighPerformance, StringComparison.OrdinalIgnoreCase);
    }
}
