using System.Text.Json;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public static class AppSettingsMigrator
{
    private const int MinimumSupportedSchemaPatch = 17;

    public static bool SupportsSourceVersion(string? sourceVersion)
    {
        if (string.IsNullOrWhiteSpace(sourceVersion)
            || !Version.TryParse(sourceVersion, out var parsed)
            || !Version.TryParse(AppSettingsDefaults.CurrentVersion, out var current))
        {
            return false;
        }

        return parsed.Revision < 0
            && parsed.Major == current.Major
            && parsed.Minor == current.Minor
            && parsed.Build >= MinimumSupportedSchemaPatch
            && parsed <= current
            && string.Equals(
                sourceVersion,
                $"{parsed.Major}.{parsed.Minor}.{parsed.Build}",
                StringComparison.Ordinal);
    }

    public static AppSettings MigrateForRead(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var sourceVersion)
            || sourceVersion.ValueKind != JsonValueKind.String)
        {
            return AppSettingsDefaults.Create();
        }

        var defaults = AppSettingsDefaults.Create();
        var sourceVersionValue = sourceVersion.GetString();
        var sourceIsVersion117 = string.Equals(
            sourceVersionValue,
            "1.0.17",
            StringComparison.Ordinal);
        var sourceUsesHostManagerCoordinatorKeys =
            SupportsSourceVersion(sourceVersionValue)
                && !sourceIsVersion117;
        var hostManagerSmartCoordinatorScoreOnlyEnabled = sourceUsesHostManagerCoordinatorKeys
            ? ReadBoolean(root, "debug", "hostManagerSmartCoordinatorScoreOnlyEnabled", defaults.Debug.HostManagerSmartCoordinatorScoreOnlyEnabled)
            : sourceIsVersion117
                && ReadBoolean(root, "debug", "smartOptimizationScoreOnlyEnabled", defaults.Debug.HostManagerSmartCoordinatorScoreOnlyEnabled);
        var hostManagerSmartCoordinatorPerformanceLogEnabled = sourceUsesHostManagerCoordinatorKeys
            ? ReadBoolean(root, "debug", "hostManagerSmartCoordinatorPerformanceLogEnabled", defaults.Debug.HostManagerSmartCoordinatorPerformanceLogEnabled)
            : sourceIsVersion117
                && ReadBoolean(root, "debug", "smartOptimizationPerformanceLogEnabled", defaults.Debug.HostManagerSmartCoordinatorPerformanceLogEnabled);

        var settings = new AppSettings(
            AppSettingsDefaults.CurrentVersion,
            new AppPerformanceSettings(
                ReadString(root, "performance", "optimizationMode", defaults.Performance.OptimizationMode),
                ReadBoolean(root, "performance", "preciseGpuPlacementEnabled", defaults.Performance.PreciseGpuPlacementEnabled),
                ReadByte(root, "performance", "gpuOverflowUsagePercent", defaults.Performance.GpuOverflowUsagePercent),
                ReadByte(root, "performance", "gpuOverflowDedicatedMemoryPercent", defaults.Performance.GpuOverflowDedicatedMemoryPercent),
                ReadByte(root, "performance", "physicalMemoryAutomaticCleanupPercent", defaults.Performance.PhysicalMemoryAutomaticCleanupPercent),
                ReadByte(root, "performance", "virtualMemoryAutomaticCleanupPercent", defaults.Performance.VirtualMemoryAutomaticCleanupPercent),
                ReadByte(root, "performance", "physicalMemoryOptimizationTargetUsagePercent", defaults.Performance.PhysicalMemoryOptimizationTargetUsagePercent),
                ReadByte(root, "performance", "virtualMemoryOptimizationTargetUsagePercent", defaults.Performance.VirtualMemoryOptimizationTargetUsagePercent),
                ReadString(root, "performance", "smartMonitoringMode", defaults.Performance.SmartMonitoringMode),
                ReadLogicRefreshInterval(
                    root,
                    "monitorRefreshIntervalMs",
                    defaults.Performance.MonitorRefreshIntervalMs),
                ReadLogicRefreshInterval(
                    root,
                    "resourceTableRefreshIntervalMs",
                    defaults.Performance.ResourceTableRefreshIntervalMs),
                ReadLogicRefreshInterval(
                    root,
                    "localSystemRefreshIntervalMs",
                    defaults.Performance.LocalSystemRefreshIntervalMs)),
            new AppAppearanceSettings(
                ReadString(root, "appearance", "uiStyle", defaults.Appearance.UiStyle),
                ReadString(root, "appearance", "theme", defaults.Appearance.Theme),
                ReadString(root, "appearance", "animations", defaults.Appearance.Animations),
                ReadString(root, "appearance", "resourceBarFrameRateMode", defaults.Appearance.ResourceBarFrameRateMode),
                ReadInt32(root, "appearance", "resourceBarFrameRate", defaults.Appearance.ResourceBarFrameRate),
                ReadString(root, "appearance", "barColorMode", defaults.Appearance.BarColorMode),
                ReadString(root, "appearance", "fontSmoothing", defaults.Appearance.FontSmoothing),
                ReadString(root, "appearance", "language", defaults.Appearance.Language),
                ReadString(root, "appearance", "byteUnitMode", defaults.Appearance.ByteUnitMode),
                ReadString(root, "appearance", "readingSourceMode", defaults.Appearance.ReadingSourceMode)),
            new AppSystemIntegrationSettings(
                ReadBoolean(root, "systemIntegration", "taskManagerShortcutReplacementEnabled", defaults.SystemIntegration.TaskManagerShortcutReplacementEnabled),
                ReadEditableHotkeys(root, defaults.SystemIntegration.Hotkeys),
                ReadBoolean(root, "systemIntegration", "autoStartEnabled", false)),
            new AppDebugSettings(
                ReadBoolean(root, "debug", "debugModeEnabled", defaults.Debug.DebugModeEnabled),
                ReadBoolean(root, "debug", "debugLogEnabled", defaults.Debug.DebugLogEnabled),
                hostManagerSmartCoordinatorScoreOnlyEnabled,
                hostManagerSmartCoordinatorPerformanceLogEnabled),
            new AppLocalPublicServiceSettings(
                ReadBoolean(root, "publicService", "enabled", defaults.PublicService.Enabled),
                ReadBoolean(root, "publicService", "fileIndexEnabled", defaults.PublicService.FileIndexEnabled),
                ReadBoolean(root, "publicService", "databaseServiceEnabled", defaults.PublicService.DatabaseServiceEnabled),
                ReadBoolean(root, "publicService", "aiModelCatalogEnabled", defaults.PublicService.AiModelCatalogEnabled)),
            new AppAiModelServiceSettings(
                Provider: defaults.AiModelService.Provider,
                Endpoint: ReadString(root, "aiModelService", "endpoint", defaults.AiModelService.Endpoint),
                AutoStartEnabled: ReadBoolean(root, "aiModelService", "autoStartEnabled", defaults.AiModelService.AutoStartEnabled)),
            new AppUpdateSettings(
                ReadBoolean(root, "update", "autoUpdateEnabled", defaults.Update!.AutoUpdateEnabled),
                ReadInt32(root, "update", "checkIntervalHours", defaults.Update.CheckIntervalHours)),
            new AppHardwareControlSettings(
                ReadInt32(root, "control", "reassertionIntervalSeconds", 120)),
            ReadReports(root));

        return AppSettingsNormalizer.Normalize(settings);
    }

    // A malformed override list is a malformed settings file (JsonException), not a reason to drop entries.
    private static AppReportSettings ReadReports(JsonElement root)
        => root.TryGetProperty("reports", out var reports) && reports.ValueKind == JsonValueKind.Object
            && reports.TryGetProperty("ruleOverrides", out var overrides) && overrides.ValueKind == JsonValueKind.Array
                ? new AppReportSettings(overrides.Deserialize<AppReportRuleOverride[]>(WebJsonOptions) ?? [])
                : new AppReportSettings([]);

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    public static bool RequiresRewrite(JsonElement root)
    {
        if (!root.TryGetProperty("control", out var control) || control.ValueKind != JsonValueKind.Object
            || !control.TryGetProperty("reassertionIntervalSeconds", out var interval)
            || interval.ValueKind != JsonValueKind.Number || !interval.TryGetInt32(out var seconds) || seconds is < 1 or > 86400) return true;
        if (!root.TryGetProperty("reports", out var reports) || reports.ValueKind != JsonValueKind.Object
            || !reports.TryGetProperty("ruleOverrides", out var ruleOverrides)
            || ruleOverrides.ValueKind != JsonValueKind.Array) return true;
        if (!root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.String
            || !string.Equals(version.GetString(), AppSettingsDefaults.CurrentVersion, StringComparison.Ordinal)
            || !root.TryGetProperty("appearance", out var appearance)
            || appearance.ValueKind != JsonValueKind.Object
            || !appearance.TryGetProperty("uiStyle", out _)
            || !appearance.TryGetProperty("animations", out _)
            || appearance.TryGetProperty("resourceBarHardwareAccelerationEnabled", out _)
            || appearance.TryGetProperty("resourceBarHardwareAccelerationMode", out _)
            || !appearance.TryGetProperty("resourceBarFrameRateMode", out _)
            || !appearance.TryGetProperty("resourceBarFrameRate", out _)
            || !appearance.TryGetProperty("barColorMode", out _)
            || !appearance.TryGetProperty("fontSmoothing", out _)
            || !appearance.TryGetProperty("language", out var language)
            || !appearance.TryGetProperty("readingSourceMode", out _)
            || appearance.TryGetProperty("sceneReferenceWhiteLog2Q16", out _)
            || appearance.TryGetProperty("outputDisplayProfiles", out _)
            || appearance.TryGetProperty("outputMinimumNits", out _)
            || appearance.TryGetProperty("outputMaximumNits", out _)
            || appearance.TryGetProperty("outputColorGamut", out _)
            || appearance.TryGetProperty("outputToneMappingCurve", out _)
            || appearance.TryGetProperty("outputBackendId", out _)
            || appearance.TryGetProperty("outputFormatId", out _)
            || !root.TryGetProperty("systemIntegration", out var systemIntegration)
            || systemIntegration.ValueKind != JsonValueKind.Object
            || !systemIntegration.TryGetProperty("taskManagerShortcutReplacementEnabled", out _)
            || !systemIntegration.TryGetProperty("autoStartEnabled", out var autoStart)
            || autoStart.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("debug", out var debug)
            || debug.ValueKind != JsonValueKind.Object
            || root.TryGetProperty("selfOptimization", out _)
            || !root.TryGetProperty("update", out var update)
            || update.ValueKind != JsonValueKind.Object
            || !update.TryGetProperty("autoUpdateEnabled", out var autoUpdateEnabled)
            || !IsBoolean(autoUpdateEnabled)
            || !update.TryGetProperty("checkIntervalHours", out var checkInterval)
            || checkInterval.ValueKind != JsonValueKind.Number
            || !checkInterval.TryGetInt32(out var checkHours) || checkHours is < 1 or > 168
            || !root.TryGetProperty("performance", out var performance)
            || performance.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (!IsSupportedLanguage(language))
        {
            return true;
        }

        if (performance.TryGetProperty("smartMonitoringEnabled", out _)
            || performance.TryGetProperty("monitoringIdleSeconds", out _)
            || !performance.TryGetProperty("optimizationMode", out var optimizationMode)
            || performance.TryGetProperty("pauseFrontendRefreshWhenHiddenInNormalMode", out _)
            || !performance.TryGetProperty("preciseGpuPlacementEnabled", out _)
            || performance.TryGetProperty("samplingDispatchMode", out _)
            || !performance.TryGetProperty("gpuOverflowUsagePercent", out var gpuUsagePercent)
            || !performance.TryGetProperty("gpuOverflowDedicatedMemoryPercent", out var gpuMemoryPercent)
            || performance.TryGetProperty("gpuPerformanceUseCases", out _)
            || performance.TryGetProperty("gpuPerformanceProfile", out _)
            || !performance.TryGetProperty("smartMonitoringMode", out var smartMonitoringMode)
            || performance.TryGetProperty("frontendHiddenRefreshMode", out _)
            || !performance.TryGetProperty("monitorRefreshIntervalMs", out var monitorRefreshIntervalMs)
            || !performance.TryGetProperty("resourceTableRefreshIntervalMs", out var resourceTableRefreshIntervalMs)
            || performance.TryGetProperty("managementRefreshIntervalMs", out _)
            || performance.TryGetProperty("discoveryRefreshIntervalMs", out _)
            || performance.TryGetProperty("optimizationRefreshIntervalMs", out _)
            || !performance.TryGetProperty("localSystemRefreshIntervalMs", out var localSystemRefreshIntervalMs))
        {
            return true;
        }

        if (!debug.TryGetProperty("debugModeEnabled", out var debugModeEnabled)
            || !debug.TryGetProperty("debugLogEnabled", out var debugLogEnabled)
            || debug.TryGetProperty("smartOptimizationScoreOnlyEnabled", out _)
            || debug.TryGetProperty("smartOptimizationPerformanceLogEnabled", out _)
            || !debug.TryGetProperty(
                "hostManagerSmartCoordinatorScoreOnlyEnabled",
                out var hostManagerSmartCoordinatorScoreOnlyEnabled)
            || !debug.TryGetProperty(
                "hostManagerSmartCoordinatorPerformanceLogEnabled",
                out var hostManagerSmartCoordinatorPerformanceLogEnabled))
        {
            return true;
        }

        return !IsSupportedOptimizationMode(optimizationMode)
            || !IsSupportedGpuOverflowPercent(gpuUsagePercent)
            || !IsSupportedGpuOverflowPercent(gpuMemoryPercent)
            || !IsSupportedAdaptiveBooleanMode(smartMonitoringMode)
            || !IsSupportedLogicRefreshInterval(monitorRefreshIntervalMs)
            || !IsSupportedLogicRefreshInterval(resourceTableRefreshIntervalMs)
            || !IsSupportedLogicRefreshInterval(localSystemRefreshIntervalMs)
            || !IsBoolean(debugModeEnabled)
            || !IsBoolean(debugLogEnabled)
            || !IsBoolean(hostManagerSmartCoordinatorScoreOnlyEnabled)
            || !IsBoolean(hostManagerSmartCoordinatorPerformanceLogEnabled);
    }

    private static bool IsBoolean(JsonElement value)
    {
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool IsSupportedLanguage(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return AppLanguageModes.Supported.Contains(value.GetString() ?? string.Empty);
    }

    private static bool IsSupportedOptimizationMode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return AppOptimizationModes.IsSupported(value.GetString());
    }

    private static bool IsSupportedAdaptiveBooleanMode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return value.GetString() is AppAdaptiveBooleanModes.Enabled
            or AppAdaptiveBooleanModes.Disabled;
    }

    private static bool IsSupportedLogicRefreshInterval(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("mode", out var mode)
            || !value.TryGetProperty("preset", out var preset)
            || !value.TryGetProperty("customValue", out var customValue))
        {
            return false;
        }

        return IsSupportedPresetNumericMode(mode)
            && IsSupportedLogicRefreshIntervalPreset(preset)
            && IsSupportedLogicRefreshIntervalMs(customValue);
    }

    private static bool IsSupportedPresetNumericMode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return value.GetString() is AppPresetNumericSettingModes.Aotu
            or AppPresetNumericSettingModes.Preset
            or AppPresetNumericSettingModes.Custom;
    }

    private static bool IsSupportedLogicRefreshIntervalPreset(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return value.GetString() is AppLogicRefreshIntervalPresets.Responsive
            or AppLogicRefreshIntervalPresets.Balanced
            or AppLogicRefreshIntervalPresets.LowPower
            or AppLogicRefreshIntervalPresets.Quiet;
    }

    private static bool IsSupportedLogicRefreshIntervalMs(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var milliseconds)
            && milliseconds >= AppSettingsDefaults.MinLogicRefreshIntervalMs
            && milliseconds <= AppSettingsDefaults.MaxLogicRefreshIntervalMs;
    }

    private static bool IsSupportedGpuOverflowPercent(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var percent) && percent is >= 1 and <= 100;

    private static AppPresetNumericSetting ReadLogicRefreshInterval(
        JsonElement root,
        string propertyName,
        AppPresetNumericSetting fallback)
    {
        if (!root.TryGetProperty("performance", out var performance)
            || performance.ValueKind != JsonValueKind.Object
            || !performance.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        var setting = new AppPresetNumericSetting(
            ReadString(value, "mode", fallback.Mode),
            ReadString(value, "preset", fallback.Preset),
            ReadInt32(value, "customValue", fallback.CustomValue));
        return AppSettingsNormalizer.NormalizeLogicRefreshInterval(
            setting,
            fallback.Preset,
            fallback.CustomValue);
    }

    private static IReadOnlyList<AppEditableHotkeySettings> ReadEditableHotkeys(
        JsonElement root,
        IReadOnlyList<AppEditableHotkeySettings> fallback)
    {
        if (!root.TryGetProperty("systemIntegration", out var systemIntegration)
            || systemIntegration.ValueKind != JsonValueKind.Object
            || !systemIntegration.TryGetProperty("hotkeys", out var hotkeys)
            || hotkeys.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        var result = new List<AppEditableHotkeySettings>();
        foreach (var hotkey in hotkeys.EnumerateArray())
        {
            if (hotkey.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var actionId = ReadString(hotkey, "actionId", string.Empty);
            var enabled = hotkey.TryGetProperty("enabled", out var enabledValue)
                && enabledValue.ValueKind == JsonValueKind.True;
            var encoding = hotkey.TryGetProperty("encoding", out var encodingValue)
                && encodingValue.ValueKind == JsonValueKind.Array
                ? encodingValue.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _))
                    .Select(static item => item.GetInt32())
                    .ToArray()
                : [];
            result.Add(new AppEditableHotkeySettings(actionId, enabled, encoding));
        }

        return result;
    }

    private static bool ReadBoolean(JsonElement root, string sectionName, string propertyName, bool fallback)
    {
        if (!root.TryGetProperty(sectionName, out var section)
            || section.ValueKind != JsonValueKind.Object
            || !section.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }

    private static string ReadString(JsonElement root, string sectionName, string propertyName, string fallback)
    {
        if (!root.TryGetProperty(sectionName, out var section)
            || section.ValueKind != JsonValueKind.Object
            || !section.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        return value.GetString() ?? fallback;
    }

    private static int ReadInt32(JsonElement root, string sectionName, string propertyName, int fallback)
    {
        if (!root.TryGetProperty(sectionName, out var section)
            || section.ValueKind != JsonValueKind.Object
            || !section.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : fallback;
    }

    private static string ReadString(JsonElement root, string propertyName, string fallback)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        return value.GetString() ?? fallback;
    }

    private static int ReadInt32(JsonElement root, string propertyName, int fallback)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : fallback;
    }

    private static byte ReadByte(JsonElement root, string sectionName, string propertyName, byte fallback)
    {
        if (!root.TryGetProperty(sectionName, out var section)
            || section.ValueKind != JsonValueKind.Object
            || !section.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetByte(out var number)
            ? number
            : fallback;
    }

}
