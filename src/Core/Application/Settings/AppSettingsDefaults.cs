using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Units;
using ResourceManager.Shared.Appearance;

namespace ResourceManager.App.Application.Settings;

public static class AppSettingsDefaults
{
    public const string CurrentVersion = "1.0.31";
    public const int RealtimeActionBudgetCount = 32;
    public const int MinRealtimeActionBudgetCount = 1;
    public const int MaxRealtimeActionBudgetCount = 8192;
    public const int RealtimeActionBudgetPercent = 5;
    public const int MinRealtimeActionBudgetPercent = 1;
    public const int MaxRealtimeActionBudgetPercent = 100;
    public const int MinLogicRefreshIntervalMs = AppFrontendRefreshIntervalPolicy.MinIntervalMs;
    public const int MaxLogicRefreshIntervalMs = AppFrontendRefreshIntervalPolicy.MaxIntervalMs;
    public const int MonitorRefreshResponsiveIntervalMs = AppFrontendRefreshIntervalPolicy.MonitorResponsiveIntervalMs;
    public const int ResourceTableRefreshResponsiveIntervalMs = AppFrontendRefreshIntervalPolicy.ResourceTableResponsiveIntervalMs;
    public const int LocalSystemRefreshBalancedIntervalMs = AppFrontendRefreshIntervalPolicy.LocalSystemBalancedIntervalMs;
    public const byte GpuOverflowUsagePercent = 95;
    public const byte GpuOverflowDedicatedMemoryPercent = 80;
    public const byte PhysicalMemoryAutomaticCleanupPercent = 6;
    public const byte VirtualMemoryAutomaticCleanupPercent = 8;
    public const byte PhysicalMemoryOptimizationTargetUsagePercent = 70;
    public const byte VirtualMemoryOptimizationTargetUsagePercent = 70;
    public const byte MinAdaptedResourceDangerPercent = 0;
    public const byte MaxAdaptedResourceDangerPercent = 95;
    public const int ResourceBarFrameRate = 60;
    public const int MinResourceBarFrameRate = 10;
    public const int MaxResourceBarFrameRate = 240;

    public static AppSettings Create()
    {
        var defaultAppearance = UiStyleCatalog.Normalize(null, null);
        return new AppSettings(
            CurrentVersion,
            new AppPerformanceSettings(
                OptimizationMode: AppOptimizationModes.Normal,
                PreciseGpuPlacementEnabled: false,
                GpuOverflowUsagePercent: GpuOverflowUsagePercent,
                GpuOverflowDedicatedMemoryPercent: GpuOverflowDedicatedMemoryPercent,
                PhysicalMemoryAutomaticCleanupPercent: PhysicalMemoryAutomaticCleanupPercent,
                VirtualMemoryAutomaticCleanupPercent: VirtualMemoryAutomaticCleanupPercent,
                PhysicalMemoryOptimizationTargetUsagePercent: PhysicalMemoryOptimizationTargetUsagePercent,
                VirtualMemoryOptimizationTargetUsagePercent: VirtualMemoryOptimizationTargetUsagePercent,
                SmartMonitoringMode: AppAdaptiveBooleanModes.Enabled,
                MonitorRefreshIntervalMs: AppFrontendRefreshIntervalPolicy.Defaults.Monitor,
                ResourceTableRefreshIntervalMs: AppFrontendRefreshIntervalPolicy.Defaults.ResourceTable,
                LocalSystemRefreshIntervalMs: AppFrontendRefreshIntervalPolicy.Defaults.LocalSystem,
                RealtimeActionBudgetMode: AppRealtimeActionBudgetModes.Fixed,
                RealtimeActionBudgetCount: RealtimeActionBudgetCount,
                RealtimeActionBudgetPercent: RealtimeActionBudgetPercent),
            new AppAppearanceSettings(
                defaultAppearance.UiStyle,
                defaultAppearance.Theme,
                AppAnimationModes.Auto,
                AppResourceBarFrameRateModes.Display,
                ResourceBarFrameRate,
                AppBarColorModes.Type,
                AppFontSmoothingModes.Auto,
                AppLanguageModes.System,
                AppByteUnitModes.Native,
                AppReadingSourceModes.Preferred),
            new AppSystemIntegrationSettings(
                TaskManagerShortcutReplacementEnabled: false,
                Hotkeys:
                [
                    new AppEditableHotkeySettings(
                        AppSystemIntegrationActionIds.ForceTerminateUnresponsiveAndForeground,
                        Enabled: false,
                        Encoding: [0, 0, 0, 0, 0, 0, 0])
                ]),
            new AppDebugSettings(
                DebugModeEnabled: false,
                DebugLogEnabled: false,
                HostManagerSmartCoordinatorScoreOnlyEnabled: false,
                HostManagerSmartCoordinatorPerformanceLogEnabled: false),
            new AppLocalPublicServiceSettings(
                Enabled: false,
                FileIndexEnabled: false,
                DatabaseServiceEnabled: false,
                AiModelCatalogEnabled: false),
            new AppAiModelServiceSettings(
                Provider: "lm-studio",
                Endpoint: "http://127.0.0.1:1234",
                AutoStartEnabled: false),
            new AppUpdateSettings(AutoUpdateEnabled: false),
            new AppHardwareControlSettings(),
            new AppReportSettings([]));
    }

    public static AppPresetNumericSetting CreateLogicRefreshIntervalDefault(
        string preset,
        int customValue)
    {
        return new AppPresetNumericSetting(
            AppPresetNumericSettingModes.Aotu,
            preset,
            customValue);
    }

}
