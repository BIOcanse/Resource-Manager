using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public static class AppSettingsDefaults
{
    public const string CurrentVersion = "1.0.24";
    public const int MonitoringIdleSeconds = 5;
    public const int MinLogicRefreshIntervalMs = 500;
    public const int MaxLogicRefreshIntervalMs = 300_000;
    public const int MonitorRefreshResponsiveIntervalMs = 1_000;
    public const int ResourceTableRefreshResponsiveIntervalMs = 1_000;
    public const int ManagementRefreshBalancedIntervalMs = 10_000;
    public const int DiscoveryRefreshResponsiveIntervalMs = 3_000;
    public const int OptimizationRefreshBalancedIntervalMs = 10_000;
    public const int LocalSystemRefreshBalancedIntervalMs = 60_000;
    public const byte VramMoveDownPhysicalMemoryDangerPercent = 10;
    public const byte PhysicalMemoryMoveDownVirtualMemoryDangerPercent = 12;
    public const byte PhysicalMemoryAutomaticCleanupPercent = 6;
    public const byte VirtualMemoryAutomaticCleanupPercent = 8;
    public const byte PhysicalMemoryOptimizationTargetUsagePercent = 70;
    public const byte VirtualMemoryOptimizationTargetUsagePercent = 70;
    public const byte MinAdaptedResourceDangerPercent = 0;
    public const byte MaxAdaptedResourceDangerPercent = 95;

    public static AppSettings Create()
    {
        return new AppSettings(
            CurrentVersion,
            new AppPerformanceSettings(
                SmartMonitoringEnabled: true,
                MonitoringIdleSeconds: MonitoringIdleSeconds,
                OptimizationMode: AppOptimizationModes.Normal,
                PauseFrontendRefreshWhenHiddenInNormalMode: true,
                PreciseGpuPlacementEnabled: true,
                VramMoveDownPhysicalMemoryDangerPercent: VramMoveDownPhysicalMemoryDangerPercent,
                PhysicalMemoryMoveDownVirtualMemoryDangerPercent: PhysicalMemoryMoveDownVirtualMemoryDangerPercent,
                PhysicalMemoryAutomaticCleanupPercent: PhysicalMemoryAutomaticCleanupPercent,
                VirtualMemoryAutomaticCleanupPercent: VirtualMemoryAutomaticCleanupPercent,
                PhysicalMemoryOptimizationTargetUsagePercent: PhysicalMemoryOptimizationTargetUsagePercent,
                VirtualMemoryOptimizationTargetUsagePercent: VirtualMemoryOptimizationTargetUsagePercent,
                GpuPerformanceUseCases: [AppGpuPerformanceUseCases.General],
                SmartMonitoringMode: AppAdaptiveBooleanModes.Auto,
                FrontendHiddenRefreshMode: AppFrontendHiddenRefreshModes.Auto,
                AutomaticSchedulingOptimizationsEnabled: true,
                MonitorRefreshIntervalMs: CreateLogicRefreshIntervalDefault(
                    AppLogicRefreshIntervalPresets.Responsive,
                    MonitorRefreshResponsiveIntervalMs),
                ResourceTableRefreshIntervalMs: CreateLogicRefreshIntervalDefault(
                    AppLogicRefreshIntervalPresets.Responsive,
                    ResourceTableRefreshResponsiveIntervalMs),
                ManagementRefreshIntervalMs: CreateLogicRefreshIntervalDefault(
                    AppLogicRefreshIntervalPresets.Balanced,
                    ManagementRefreshBalancedIntervalMs),
                DiscoveryRefreshIntervalMs: CreateLogicRefreshIntervalDefault(
                    AppLogicRefreshIntervalPresets.Responsive,
                    DiscoveryRefreshResponsiveIntervalMs),
                OptimizationRefreshIntervalMs: CreateLogicRefreshIntervalDefault(
                    AppLogicRefreshIntervalPresets.Balanced,
                    OptimizationRefreshBalancedIntervalMs),
                LocalSystemRefreshIntervalMs: CreateLogicRefreshIntervalDefault(
                    AppLogicRefreshIntervalPresets.Balanced,
                    LocalSystemRefreshBalancedIntervalMs)),
            new AppAppearanceSettings(
                AppThemeModes.System,
                AppAnimationModes.Auto,
                true,
                AppAdaptiveBooleanModes.Auto,
                AppBarColorModes.Type,
                AppFontSmoothingModes.Auto,
                AppLanguageModes.System),
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
                AutoStartEnabled: false));
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
