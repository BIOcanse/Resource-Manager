using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public static class AppSettingsNormalizer
{
    public static AppSettings Normalize(AppSettings? settings)
    {
        if (settings is null)
        {
            return AppSettingsDefaults.Create();
        }

        return new AppSettings(
            AppSettingsDefaults.CurrentVersion,
            NormalizePerformance(settings.Performance),
            NormalizeAppearance(settings.Appearance),
            NormalizeSystemIntegration(settings.SystemIntegration),
            NormalizeDebug(settings.Debug),
            NormalizePublicService(settings.PublicService),
            NormalizeAiModelService(settings.AiModelService));
    }

    private static AppPerformanceSettings NormalizePerformance(AppPerformanceSettings? performance)
    {
        return new AppPerformanceSettings(
            performance?.SmartMonitoringEnabled ?? true,
            AppSettingsDefaults.MonitoringIdleSeconds,
            NormalizeOptimizationMode(performance?.OptimizationMode),
            performance?.PauseFrontendRefreshWhenHiddenInNormalMode ?? true,
            performance?.PreciseGpuPlacementEnabled ?? AppSettingsDefaults.Create().Performance.PreciseGpuPlacementEnabled,
            NormalizeDangerPercent(performance?.VramMoveDownPhysicalMemoryDangerPercent, AppSettingsDefaults.VramMoveDownPhysicalMemoryDangerPercent),
            NormalizeDangerPercent(performance?.PhysicalMemoryMoveDownVirtualMemoryDangerPercent, AppSettingsDefaults.PhysicalMemoryMoveDownVirtualMemoryDangerPercent),
            NormalizeDangerPercent(performance?.PhysicalMemoryAutomaticCleanupPercent, AppSettingsDefaults.PhysicalMemoryAutomaticCleanupPercent),
            NormalizeDangerPercent(performance?.VirtualMemoryAutomaticCleanupPercent, AppSettingsDefaults.VirtualMemoryAutomaticCleanupPercent),
            NormalizeTargetUsagePercent(performance?.PhysicalMemoryOptimizationTargetUsagePercent, AppSettingsDefaults.PhysicalMemoryOptimizationTargetUsagePercent),
            NormalizeTargetUsagePercent(performance?.VirtualMemoryOptimizationTargetUsagePercent, AppSettingsDefaults.VirtualMemoryOptimizationTargetUsagePercent),
            NormalizeGpuPerformanceUseCases(performance?.GpuPerformanceUseCases),
            NormalizeAdaptiveBooleanMode(performance?.SmartMonitoringMode),
            NormalizeFrontendHiddenRefreshMode(performance?.FrontendHiddenRefreshMode),
            performance?.AutomaticSchedulingOptimizationsEnabled
                ?? AppSettingsDefaults.Create().Performance.AutomaticSchedulingOptimizationsEnabled,
            NormalizeLogicRefreshInterval(
                performance?.MonitorRefreshIntervalMs,
                AppLogicRefreshIntervalPresets.Responsive,
                AppSettingsDefaults.MonitorRefreshResponsiveIntervalMs),
            NormalizeLogicRefreshInterval(
                performance?.ResourceTableRefreshIntervalMs,
                AppLogicRefreshIntervalPresets.Responsive,
                AppSettingsDefaults.ResourceTableRefreshResponsiveIntervalMs),
            NormalizeLogicRefreshInterval(
                performance?.ManagementRefreshIntervalMs,
                AppLogicRefreshIntervalPresets.Balanced,
                AppSettingsDefaults.ManagementRefreshBalancedIntervalMs),
            NormalizeLogicRefreshInterval(
                performance?.DiscoveryRefreshIntervalMs,
                AppLogicRefreshIntervalPresets.Responsive,
                AppSettingsDefaults.DiscoveryRefreshResponsiveIntervalMs),
            NormalizeLogicRefreshInterval(
                performance?.OptimizationRefreshIntervalMs,
                AppLogicRefreshIntervalPresets.Balanced,
                AppSettingsDefaults.OptimizationRefreshBalancedIntervalMs),
            NormalizeLogicRefreshInterval(
                performance?.LocalSystemRefreshIntervalMs,
                AppLogicRefreshIntervalPresets.Balanced,
                AppSettingsDefaults.LocalSystemRefreshBalancedIntervalMs));
    }

    private static AppAppearanceSettings NormalizeAppearance(AppAppearanceSettings? appearance)
    {
        return new AppAppearanceSettings(
            NormalizeTheme(appearance?.Theme),
            NormalizeAnimations(appearance?.Animations),
            appearance?.ResourceBarHardwareAccelerationEnabled ?? true,
            NormalizeAdaptiveBooleanMode(appearance?.ResourceBarHardwareAccelerationMode),
            NormalizeBarColorMode(appearance?.BarColorMode),
            NormalizeFontSmoothing(appearance?.FontSmoothing),
            NormalizeLanguage(appearance?.Language));
    }

    private static AppSystemIntegrationSettings NormalizeSystemIntegration(AppSystemIntegrationSettings? systemIntegration)
    {
        return new AppSystemIntegrationSettings(
            systemIntegration?.TaskManagerShortcutReplacementEnabled ?? false,
            NormalizeEditableHotkeys(systemIntegration?.Hotkeys),
            systemIntegration?.AutoStartEnabled ?? false);
    }

    private static AppLocalPublicServiceSettings NormalizePublicService(
        AppLocalPublicServiceSettings? publicService)
    {
        return new AppLocalPublicServiceSettings(
            publicService?.Enabled ?? false,
            publicService?.FileIndexEnabled ?? false,
            publicService?.DatabaseServiceEnabled ?? false,
            publicService?.AiModelCatalogEnabled ?? false);
    }

    private static AppAiModelServiceSettings NormalizeAiModelService(
        AppAiModelServiceSettings? aiModelService)
    {
        return new AppAiModelServiceSettings(
            Provider: "lm-studio",
            Endpoint: NormalizeLmStudioEndpoint(aiModelService?.Endpoint),
            AutoStartEnabled: aiModelService?.AutoStartEnabled ?? false);
    }

    private static string NormalizeLmStudioEndpoint(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || !uri.IsLoopback
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return "http://127.0.0.1:1234";
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static IReadOnlyList<AppEditableHotkeySettings> NormalizeEditableHotkeys(
        IEnumerable<AppEditableHotkeySettings>? hotkeys)
    {
        var normalized = (hotkeys ?? [])
            .Where(static hotkey => hotkey is not null && !string.IsNullOrWhiteSpace(hotkey.ActionId))
            .Select(static hotkey =>
            {
                var encoding = NormalizeHotkeyEncoding(hotkey.Encoding);
                var enabled = hotkey.Enabled && encoding[0] != 0;
                if (enabled
                    && string.Equals(
                        hotkey.ActionId,
                        AppSystemIntegrationActionIds.ForceTerminateUnresponsiveAndForeground,
                        StringComparison.Ordinal)
                    && !AppSettingsHotkeySafetyValidator.IsSafeDestructiveEncoding(encoding))
                {
                    enabled = false;
                }
                return new AppEditableHotkeySettings(
                    hotkey.ActionId.Trim(),
                    enabled,
                    encoding);
            })
            .GroupBy(static hotkey => hotkey.ActionId, StringComparer.Ordinal)
            .Select(static group => group.First())
            .Take(32)
            .ToList();

        if (normalized.All(static hotkey =>
                !string.Equals(
                    hotkey.ActionId,
                    AppSystemIntegrationActionIds.ForceTerminateUnresponsiveAndForeground,
                    StringComparison.Ordinal)))
        {
            normalized.Add(new AppEditableHotkeySettings(
                AppSystemIntegrationActionIds.ForceTerminateUnresponsiveAndForeground,
                Enabled: false,
                Encoding: [0, 0, 0, 0, 0, 0, 0]));
        }

        return normalized;
    }

    public static IReadOnlyList<int> NormalizeHotkeyEncoding(IEnumerable<int>? encoding)
    {
        var source = (encoding ?? []).Take(7).ToArray();
        var keys = new List<int>(4);
        var relations = new List<int>(3);
        for (var keyIndex = 0; keyIndex < 4; keyIndex++)
        {
            var slotIndex = keyIndex * 2;
            var virtualKey = slotIndex < source.Length ? source[slotIndex] : 0;
            if (virtualKey is <= 0 or > 255 || keys.Contains(virtualKey))
            {
                continue;
            }

            if (keys.Count > 0)
            {
                var relationIndex = slotIndex - 1;
                relations.Add(relationIndex >= 0
                    && relationIndex < source.Length
                    && source[relationIndex] == 1
                        ? 1
                        : 0);
            }

            keys.Add(virtualKey);
        }

        var result = new int[7];
        for (var index = 0; index < keys.Count; index++)
        {
            result[index * 2] = keys[index];
            if (index > 0)
            {
                result[(index * 2) - 1] = relations[index - 1];
            }
        }

        return result;
    }

    private static string NormalizeAnimations(string? animations)
    {
        return animations switch
        {
            AppAnimationModes.Auto => AppAnimationModes.Auto,
            AppAnimationModes.None => AppAnimationModes.None,
            AppAnimationModes.Ultra => AppAnimationModes.Ultra,
            _ => AppAnimationModes.Normal
        };
    }

    public static string NormalizeAdaptiveBooleanMode(string? mode)
    {
        return mode switch
        {
            AppAdaptiveBooleanModes.Enabled => AppAdaptiveBooleanModes.Enabled,
            AppAdaptiveBooleanModes.Disabled => AppAdaptiveBooleanModes.Disabled,
            _ => AppAdaptiveBooleanModes.Auto
        };
    }

    public static string NormalizeFrontendHiddenRefreshMode(string? mode)
    {
        return mode switch
        {
            AppFrontendHiddenRefreshModes.PauseWhenHidden => AppFrontendHiddenRefreshModes.PauseWhenHidden,
            AppFrontendHiddenRefreshModes.ContinueWhenHidden => AppFrontendHiddenRefreshModes.ContinueWhenHidden,
            _ => AppFrontendHiddenRefreshModes.Auto
        };
    }

    public static AppPresetNumericSetting NormalizeLogicRefreshInterval(
        AppPresetNumericSetting? setting,
        string fallbackPreset,
        int fallbackCustomValue)
    {
        return new AppPresetNumericSetting(
            NormalizePresetNumericMode(setting?.Mode),
            NormalizeLogicRefreshIntervalPreset(setting?.Preset, fallbackPreset),
            NormalizeLogicRefreshIntervalMs(setting?.CustomValue, fallbackCustomValue));
    }

    public static string NormalizePresetNumericMode(string? mode)
    {
        return mode switch
        {
            AppPresetNumericSettingModes.Preset => AppPresetNumericSettingModes.Preset,
            AppPresetNumericSettingModes.Custom => AppPresetNumericSettingModes.Custom,
            _ => AppPresetNumericSettingModes.Aotu
        };
    }

    public static string NormalizeLogicRefreshIntervalPreset(string? preset, string fallbackPreset)
    {
        var normalized = preset?.Trim();
        if (string.Equals(normalized, AppLogicRefreshIntervalPresets.Responsive, StringComparison.OrdinalIgnoreCase))
        {
            return AppLogicRefreshIntervalPresets.Responsive;
        }

        if (string.Equals(normalized, AppLogicRefreshIntervalPresets.Balanced, StringComparison.OrdinalIgnoreCase))
        {
            return AppLogicRefreshIntervalPresets.Balanced;
        }

        if (string.Equals(normalized, AppLogicRefreshIntervalPresets.LowPower, StringComparison.OrdinalIgnoreCase))
        {
            return AppLogicRefreshIntervalPresets.LowPower;
        }

        if (string.Equals(normalized, AppLogicRefreshIntervalPresets.Quiet, StringComparison.OrdinalIgnoreCase))
        {
            return AppLogicRefreshIntervalPresets.Quiet;
        }

        return AppLogicRefreshIntervalPresets.Supported.Contains(fallbackPreset)
            ? fallbackPreset
            : AppLogicRefreshIntervalPresets.Balanced;
    }

    private static string NormalizeBarColorMode(string? barColorMode)
    {
        return barColorMode == AppBarColorModes.Distinct ? AppBarColorModes.Distinct : AppBarColorModes.Type;
    }

    private static string NormalizeFontSmoothing(string? fontSmoothing)
    {
        return fontSmoothing switch
        {
            AppFontSmoothingModes.Auto => AppFontSmoothingModes.Auto,
            AppFontSmoothingModes.Grayscale => AppFontSmoothingModes.Grayscale,
            AppFontSmoothingModes.Disabled => AppFontSmoothingModes.Disabled,
            AppFontSmoothingModes.System => AppFontSmoothingModes.System,
            _ => AppFontSmoothingModes.Auto
        };
    }

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return AppLanguageModes.System;
        }

        var normalized = language.Trim();
        return AppLanguageModes.Supported.Contains(normalized)
            ? normalized
            : AppLanguageModes.System;
    }

    private static AppDebugSettings NormalizeDebug(AppDebugSettings? debug)
    {
        var debugModeEnabled = debug?.DebugModeEnabled == true;
        return new AppDebugSettings(
            debugModeEnabled,
            debug?.DebugLogEnabled == true,
            debug?.HostManagerSmartCoordinatorScoreOnlyEnabled == true,
            debug?.HostManagerSmartCoordinatorPerformanceLogEnabled == true);
    }

    private static string NormalizeTheme(string? theme)
    {
        return theme switch
        {
            AppThemeModes.Light => AppThemeModes.Light,
            AppThemeModes.Dark => AppThemeModes.Dark,
            AppThemeModes.LowContrast => AppThemeModes.LowContrast,
            _ => AppThemeModes.System
        };
    }

    private static string NormalizeOptimizationMode(string? mode)
    {
        return mode switch
        {
            AppOptimizationModes.MemoryOnly => AppOptimizationModes.MemoryOnly,
            AppOptimizationModes.Smart => AppOptimizationModes.Smart,
            _ => AppOptimizationModes.Normal
        };
    }

    public static IReadOnlyList<string> NormalizeGpuPerformanceUseCases(IEnumerable<string>? useCases)
    {
        var normalized = (useCases ?? [])
            .Where(static useCase => !string.IsNullOrWhiteSpace(useCase))
            .Select(static useCase => useCase.Trim())
            .Where(static useCase => AppGpuPerformanceUseCases.Supported.Contains(useCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length > 0
            ? normalized
            : [AppGpuPerformanceUseCases.General];
    }

    public static IReadOnlyList<string> NormalizeGpuPerformanceUseCase(string? useCase)
    {
        return useCase switch
        {
            AppGpuPerformanceUseCases.Ai => [AppGpuPerformanceUseCases.Ai],
            AppGpuPerformanceUseCases.Gaming => [AppGpuPerformanceUseCases.Gaming],
            _ => [AppGpuPerformanceUseCases.General]
        };
    }

    private static byte NormalizeDangerPercent(byte? percent, byte fallback)
    {
        return (byte)Math.Clamp(
            percent ?? fallback,
            AppSettingsDefaults.MinAdaptedResourceDangerPercent,
            AppSettingsDefaults.MaxAdaptedResourceDangerPercent);
    }

    private static byte NormalizeTargetUsagePercent(byte? percent, byte fallback)
    {
        return (byte)Math.Clamp(
            (int)(percent ?? fallback),
            5,
            95);
    }

    private static int NormalizeLogicRefreshIntervalMs(int? milliseconds, int fallback)
    {
        return Math.Clamp(
            milliseconds ?? fallback,
            AppSettingsDefaults.MinLogicRefreshIntervalMs,
            AppSettingsDefaults.MaxLogicRefreshIntervalMs);
    }
}
