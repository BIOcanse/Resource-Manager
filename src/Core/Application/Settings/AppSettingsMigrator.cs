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
                ReadString(root, "performance", "realtimeActionBudgetMode", defaults.Performance.RealtimeActionBudgetMode),
                ReadInt32(root, "performance", "realtimeActionBudgetCount", defaults.Performance.RealtimeActionBudgetCount),
                ReadInt32(root, "performance", "realtimeActionBudgetPercent", defaults.Performance.RealtimeActionBudgetPercent)),
            new AppSystemIntegrationSettings(
                ReadBoolean(root, "systemIntegration", "taskManagerShortcutReplacementEnabled", defaults.SystemIntegration.TaskManagerShortcutReplacementEnabled),
                ReadEditableHotkeys(root, defaults.SystemIntegration.Hotkeys),
                ReadBoolean(root, "systemIntegration", "autoStartEnabled", false),
                ReadBoolean(root, "systemIntegration", "restartAfterCrashEnabled", true),
                ReadInt32(root, "systemIntegration", "crashRestartDelaySeconds", 10),
                ReadInt32(root, "systemIntegration", "crashRestartCount", 3)),
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
                ReadString(root, "update", "mode",
                    ReadBoolean(root, "update", "autoUpdateEnabled", true) ? AppUpdateModes.Automatic : AppUpdateModes.Off),
                ReadBoolean(root, "update", "periodicCheckEnabled", ReadBoolean(root, "update", "autoUpdateEnabled", false)),
                ReadInt32(root, "update", "checkIntervalHours", 24)),
            new AppHardwareControlSettings(
                ReadInt32(root, "control", "reassertionIntervalSeconds", 120)),
            ReadReports(root),
            root.TryGetProperty("memoryProtection", out var protection)
                ? protection.Deserialize<HostMemoryProtectionSettings>(WebJsonOptions)
                    ?? throw new JsonException("memoryProtection must be an object.")
                : new HostMemoryProtectionSettings());

        return AppSettingsNormalizer.Normalize(settings);
    }

    // A malformed override list is a malformed settings file (JsonException), not a reason to drop entries.
    private static AppReportSettings ReadReports(JsonElement root)
        => root.TryGetProperty("reports", out var reports) && reports.ValueKind == JsonValueKind.Object
            && reports.TryGetProperty("ruleOverrides", out var overrides) && overrides.ValueKind == JsonValueKind.Array
                ? new AppReportSettings(overrides.Deserialize<AppReportRuleOverride[]>(WebJsonOptions) ?? [])
                : new AppReportSettings([]);

    private static readonly JsonSerializerOptions WebJsonOptions = new AppSettingsJsonContext(new(JsonSerializerDefaults.Web)).Options;

    public static bool RequiresRewrite(JsonElement root)
    {
        if (!root.TryGetProperty("systemIntegration", out var integration)
            || integration.ValueKind != JsonValueKind.Object
            || !integration.TryGetProperty("restartAfterCrashEnabled", out var enabled)
            || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !integration.TryGetProperty("crashRestartDelaySeconds", out var delay)
            || delay.ValueKind != JsonValueKind.Number
            || !delay.TryGetInt32(out var delaySeconds) || delaySeconds is < 1 or > 3600
            || !integration.TryGetProperty("crashRestartCount", out var count)
            || count.ValueKind != JsonValueKind.Number
            || !count.TryGetInt32(out var restarts) || restarts is < 1 or > 10) return true;
        if (!root.TryGetProperty("control", out var control) || control.ValueKind != JsonValueKind.Object
            || !control.TryGetProperty("reassertionIntervalSeconds", out var interval)
            || interval.ValueKind != JsonValueKind.Number || !interval.TryGetInt32(out var seconds) || seconds is < 1 or > 86400) return true;
        if (!root.TryGetProperty("reports", out var reports) || reports.ValueKind != JsonValueKind.Object
            || !reports.TryGetProperty("ruleOverrides", out var ruleOverrides)
            || ruleOverrides.ValueKind != JsonValueKind.Array) return true;
        if (!root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.String
            || !string.Equals(version.GetString(), AppSettingsDefaults.CurrentVersion, StringComparison.Ordinal)
            || root.TryGetProperty("appearance", out _)
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
            || update.TryGetProperty("autoUpdateEnabled", out _)
            || !update.TryGetProperty("periodicCheckEnabled", out var periodicCheck)
            || !IsBoolean(periodicCheck)
            || !update.TryGetProperty("checkIntervalHours", out var checkInterval)
            || checkInterval.ValueKind != JsonValueKind.Number
            || !checkInterval.TryGetInt32(out var checkHours) || checkHours is < 24 or > 168
            || !update.TryGetProperty("mode", out var updateMode)
            || updateMode.ValueKind != JsonValueKind.String
            || !AppUpdateModes.IsSupported(updateMode.GetString())
            || !root.TryGetProperty("performance", out var performance)
            || performance.ValueKind != JsonValueKind.Object)
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
            || performance.TryGetProperty("smartMonitoringMode", out _)
            || performance.TryGetProperty("frontendHiddenRefreshMode", out _)
            || performance.TryGetProperty("monitorRefreshIntervalMs", out _)
            || performance.TryGetProperty("resourceTableRefreshIntervalMs", out _)
            || performance.TryGetProperty("managementRefreshIntervalMs", out _)
            || performance.TryGetProperty("discoveryRefreshIntervalMs", out _)
            || performance.TryGetProperty("optimizationRefreshIntervalMs", out _)
            || performance.TryGetProperty("localSystemRefreshIntervalMs", out _)
            || !performance.TryGetProperty("realtimeActionBudgetMode", out var realtimeActionBudgetMode)
            || !performance.TryGetProperty("realtimeActionBudgetCount", out var realtimeActionBudgetCount)
            || !performance.TryGetProperty("realtimeActionBudgetPercent", out var realtimeActionBudgetPercent))
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
            || realtimeActionBudgetMode.ValueKind != JsonValueKind.String
            || !AppRealtimeActionBudgetModes.IsSupported(realtimeActionBudgetMode.GetString())
            || !IsSupportedRealtimeActionBudgetCount(realtimeActionBudgetCount)
            || !IsSupportedRealtimeActionBudgetPercent(realtimeActionBudgetPercent)
            || !IsBoolean(debugModeEnabled)
            || !IsBoolean(debugLogEnabled)
            || !IsBoolean(hostManagerSmartCoordinatorScoreOnlyEnabled)
            || !IsBoolean(hostManagerSmartCoordinatorPerformanceLogEnabled);
    }

    private static bool IsBoolean(JsonElement value)
    {
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool IsSupportedRealtimeActionBudgetCount(JsonElement value)
        => value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var count)
            && count >= AppSettingsDefaults.MinRealtimeActionBudgetCount
            && count <= AppSettingsDefaults.MaxRealtimeActionBudgetCount;

    private static bool IsSupportedRealtimeActionBudgetPercent(JsonElement value)
        => value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var percent)
            && percent >= AppSettingsDefaults.MinRealtimeActionBudgetPercent
            && percent <= AppSettingsDefaults.MaxRealtimeActionBudgetPercent;

    private static bool IsSupportedOptimizationMode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return AppOptimizationModes.IsSupported(value.GetString());
    }

    private static bool IsSupportedGpuOverflowPercent(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var percent) && percent is >= 1 and <= 100;

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
