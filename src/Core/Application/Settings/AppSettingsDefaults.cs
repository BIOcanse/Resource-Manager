using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public static class AppSettingsDefaults
{
    public const string CurrentVersion = "1.0.36";
    public const int RealtimeActionBudgetCount = 32;
    public const int MinRealtimeActionBudgetCount = 1;
    public const int MaxRealtimeActionBudgetCount = 8192;
    public const int RealtimeActionBudgetPercent = 5;
    public const int MinRealtimeActionBudgetPercent = 1;
    public const int MaxRealtimeActionBudgetPercent = 100;
    public const byte GpuOverflowUsagePercent = 95;
    public const byte GpuOverflowDedicatedMemoryPercent = 80;
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
                OptimizationMode: AppOptimizationModes.Normal,
                PreciseGpuPlacementEnabled: false,
                GpuOverflowUsagePercent: GpuOverflowUsagePercent,
                GpuOverflowDedicatedMemoryPercent: GpuOverflowDedicatedMemoryPercent,
                PhysicalMemoryAutomaticCleanupPercent: PhysicalMemoryAutomaticCleanupPercent,
                VirtualMemoryAutomaticCleanupPercent: VirtualMemoryAutomaticCleanupPercent,
                PhysicalMemoryOptimizationTargetUsagePercent: PhysicalMemoryOptimizationTargetUsagePercent,
                VirtualMemoryOptimizationTargetUsagePercent: VirtualMemoryOptimizationTargetUsagePercent,
                RealtimeActionBudgetMode: AppRealtimeActionBudgetModes.Fixed,
                RealtimeActionBudgetCount: RealtimeActionBudgetCount,
                RealtimeActionBudgetPercent: RealtimeActionBudgetPercent),
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
            new AppUpdateSettings(),
            new AppHardwareControlSettings(),
            new AppReportSettings([]),
            new HostMemoryProtectionSettings(Reserves: []));
    }

}
