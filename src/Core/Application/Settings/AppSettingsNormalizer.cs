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
            NormalizeSystemIntegration(settings.SystemIntegration),
            NormalizeDebug(settings.Debug),
            NormalizePublicService(settings.PublicService),
            NormalizeAiModelService(settings.AiModelService),
            new AppUpdateSettings(AppUpdateModes.Normalize(settings.Update?.Mode),
                settings.Update?.PeriodicCheckEnabled ?? false,
                Math.Clamp(settings.Update?.CheckIntervalHours ?? 24, 24, 168)),
            new AppHardwareControlSettings(Math.Clamp(settings.Control?.ReassertionIntervalSeconds ?? 120, 1, 86_400)),
            // Rule values are validated against the rule profile by the runtime plan compiler, not rewritten here.
            new AppReportSettings(settings.Reports?.RuleOverrides ?? []),
            (settings.MemoryProtection ?? new HostMemoryProtectionSettings()) with
                { Reserves = settings.MemoryProtection?.Reserves?.ToArray() ?? [] });
    }

    private static AppPerformanceSettings NormalizePerformance(AppPerformanceSettings? performance)
    {
        return new AppPerformanceSettings(
            NormalizeOptimizationMode(performance?.OptimizationMode),
            performance?.PreciseGpuPlacementEnabled ?? AppSettingsDefaults.Create().Performance.PreciseGpuPlacementEnabled,
            NormalizeGpuOverflowPercent(performance?.GpuOverflowUsagePercent, AppSettingsDefaults.GpuOverflowUsagePercent),
            NormalizeGpuOverflowPercent(performance?.GpuOverflowDedicatedMemoryPercent, AppSettingsDefaults.GpuOverflowDedicatedMemoryPercent),
            NormalizeDangerPercent(performance?.PhysicalMemoryAutomaticCleanupPercent, AppSettingsDefaults.PhysicalMemoryAutomaticCleanupPercent),
            NormalizeDangerPercent(performance?.VirtualMemoryAutomaticCleanupPercent, AppSettingsDefaults.VirtualMemoryAutomaticCleanupPercent),
            NormalizeTargetUsagePercent(performance?.PhysicalMemoryOptimizationTargetUsagePercent, AppSettingsDefaults.PhysicalMemoryOptimizationTargetUsagePercent),
            NormalizeTargetUsagePercent(performance?.VirtualMemoryOptimizationTargetUsagePercent, AppSettingsDefaults.VirtualMemoryOptimizationTargetUsagePercent),
            AppRealtimeActionBudgetModes.Normalize(performance?.RealtimeActionBudgetMode),
            Math.Clamp(
                performance?.RealtimeActionBudgetCount ?? AppSettingsDefaults.RealtimeActionBudgetCount,
                AppSettingsDefaults.MinRealtimeActionBudgetCount,
                AppSettingsDefaults.MaxRealtimeActionBudgetCount),
            Math.Clamp(
                performance?.RealtimeActionBudgetPercent ?? AppSettingsDefaults.RealtimeActionBudgetPercent,
                AppSettingsDefaults.MinRealtimeActionBudgetPercent,
                AppSettingsDefaults.MaxRealtimeActionBudgetPercent));
    }

    private static AppSystemIntegrationSettings NormalizeSystemIntegration(AppSystemIntegrationSettings? systemIntegration)
    {
        return new AppSystemIntegrationSettings(
            systemIntegration?.TaskManagerShortcutReplacementEnabled ?? false,
            NormalizeEditableHotkeys(systemIntegration?.Hotkeys),
            systemIntegration?.AutoStartEnabled ?? false,
            systemIntegration?.RestartAfterCrashEnabled ?? true,
            Math.Clamp(systemIntegration?.CrashRestartDelaySeconds ?? 10, 1, 3600),
            Math.Clamp(systemIntegration?.CrashRestartCount ?? 3, 1, 10));
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

    private static AppDebugSettings NormalizeDebug(AppDebugSettings? debug)
    {
        var debugModeEnabled = debug?.DebugModeEnabled == true;
        return new AppDebugSettings(
            debugModeEnabled,
            debug?.DebugLogEnabled == true,
            debug?.HostManagerSmartCoordinatorScoreOnlyEnabled == true,
            debug?.HostManagerSmartCoordinatorPerformanceLogEnabled == true);
    }

    private static string NormalizeOptimizationMode(string? mode)
        => AppOptimizationModes.Normalize(mode);

    private static byte NormalizeDangerPercent(byte? percent, byte fallback)
    {
        return (byte)Math.Clamp(
            percent ?? fallback,
            AppSettingsDefaults.MinAdaptedResourceDangerPercent,
            AppSettingsDefaults.MaxAdaptedResourceDangerPercent);
    }

    private static byte NormalizeGpuOverflowPercent(byte? percent, byte fallback)
        => (byte)Math.Clamp((int)(percent ?? fallback), 1, 100);

    private static byte NormalizeTargetUsagePercent(byte? percent, byte fallback)
    {
        return (byte)Math.Clamp(
            (int)(percent ?? fallback),
            5,
            95);
    }

}
