
namespace ResourceManager.App.Domain.Settings;

public static class AppOptimizationModes
{
    public const string Normal = "normal";
    public const string MemoryOnly = "limited";
    public const string CpuOnly = "cpu";
    public const string GpuOnly = "gpu";
    public const string MemoryCpu = "memory+cpu";
    public const string MemoryGpu = "memory+gpu";
    public const string CpuGpu = "cpu+gpu";
    public const string Smart = "smart";

    public static bool IsSupported(string? mode)
        => mode is Normal or MemoryOnly or CpuOnly or GpuOnly
            or MemoryCpu or MemoryGpu or CpuGpu or Smart;

    public static string Normalize(string? mode)
        => IsSupported(mode) ? mode! : Normal;
}

public static class AppSystemIntegrationActionIds
{
    public const string ForceTerminateUnresponsiveAndForeground = "force-terminate-unresponsive-and-foreground";
}

public sealed record AppSettings(
    string Version,
    AppPerformanceSettings Performance,
    AppSystemIntegrationSettings SystemIntegration,
    AppDebugSettings Debug,
    AppLocalPublicServiceSettings PublicService,
    AppAiModelServiceSettings AiModelService,
    AppUpdateSettings? Update = null,
    AppHardwareControlSettings? Control = null,
    AppReportSettings? Reports = null,
    HostMemoryProtectionSettings? MemoryProtection = null);

public sealed record HostMemoryProtectionSettings(
    bool Enabled = false,
    double ThresholdPercent = 95,
    double CheckIntervalSeconds = 1,
    IReadOnlyList<HostMemoryReserveSettings>? Reserves = null);

public sealed record HostMemoryReserveSettings(string Location, ulong SizeBytes);

public sealed record AppHardwareControlSettings(int ReassertionIntervalSeconds = 120);

// 用户对报告规则判定值的修改，按规则的稳定名字（default.json 的 name）对应。
// 字段为 null 表示沿用默认值；数值按规则自身的单位（°C、%、ms、字节）。
// 合并与校验由运行计划编译器负责，见 docs/optimization_reports.md。
public sealed record AppReportSettings(IReadOnlyList<AppReportRuleOverride> RuleOverrides);

public sealed record AppReportRuleOverride(
    string Rule,
    double? ActivationThreshold = null,
    double? ClearThreshold = null,
    uint? RequiredConsecutiveHits = null,
    uint? RequiredConsecutiveMisses = null,
    double? ZeroActivityThreshold = null,
    IReadOnlyList<string>? ExecutableNames = null);

public static class AppUpdateModes
{
    public const string Off = "off";
    public const string Notify = "notify";
    public const string Automatic = "automatic";

    public static bool IsSupported(string? mode) => mode is Off or Notify or Automatic;
    public static string Normalize(string? mode) => IsSupported(mode) ? mode! : Automatic;
}

public sealed record AppUpdateSettings(
    string Mode = AppUpdateModes.Automatic,
    bool PeriodicCheckEnabled = false,
    int CheckIntervalHours = 24);

public sealed record AppPerformanceSettings(
    string OptimizationMode,
    bool PreciseGpuPlacementEnabled,
    byte GpuOverflowUsagePercent,
    byte GpuOverflowDedicatedMemoryPercent,
    byte PhysicalMemoryAutomaticCleanupPercent,
    byte VirtualMemoryAutomaticCleanupPercent,
    byte PhysicalMemoryOptimizationTargetUsagePercent,
    byte VirtualMemoryOptimizationTargetUsagePercent,
    string RealtimeActionBudgetMode,
    int RealtimeActionBudgetCount,
    int RealtimeActionBudgetPercent);

public sealed record AppSystemIntegrationSettings(
    bool TaskManagerShortcutReplacementEnabled,
    IReadOnlyList<AppEditableHotkeySettings> Hotkeys,
    bool AutoStartEnabled = false,
    bool RestartAfterCrashEnabled = true,
    int CrashRestartDelaySeconds = 10,
    int CrashRestartCount = 3);

public sealed record AppEditableHotkeySettings(
    string ActionId,
    bool Enabled,
    IReadOnlyList<int> Encoding);

public sealed record AppDebugSettings(
    bool DebugModeEnabled,
    bool DebugLogEnabled,
    bool HostManagerSmartCoordinatorScoreOnlyEnabled,
    bool HostManagerSmartCoordinatorPerformanceLogEnabled);

public sealed record AppLocalPublicServiceSettings(
    bool Enabled,
    bool FileIndexEnabled,
    bool DatabaseServiceEnabled,
    bool AiModelCatalogEnabled);

public sealed record AppAiModelServiceSettings(
    string Provider,
    string Endpoint,
    bool AutoStartEnabled);

public enum AppSettingsSourceKind : byte
{
    BundledFirstRun = 0,
    Persisted = 1,
    MigratedPersisted = 2,
    SavedPersisted = 3,
    RecoveredLastKnownGood = 4,
    RecoveredDefaultsAfterCorruption = 5,
    TestFixture = 255
}

public sealed record AppSettingsSourceMetadata(
    AppSettingsSourceKind Kind,
    string SourceVersion,
    string InputSha256,
    string EffectiveSha256,
    bool RewritePerformed)
{
    public string RecoveryDisposition { get; init; } = "none";

    public string? RecoveryArtifactPath { get; init; }

    public string? LastKnownGoodPath { get; init; }
}

public sealed record AppSettingsUpdateResult(
    AppSettings Settings,
    DateTimeOffset UpdatedAt,
    string StoragePath,
    AppSettingsSourceMetadata Source)
{
    public string Revision => Source.EffectiveSha256;

    public string RuntimeApplicationDisposition { get; init; } = "notRequested";

    public long? RuntimePlanVersion { get; init; }

    public ulong? RuntimePublicationSequence { get; init; }

    public int RuntimeDeliveryFailureCount { get; init; }

    public IReadOnlyList<string> RuntimeCapabilityConstrainedPaths { get; init; } = [];

    public string? RuntimeFailureCode { get; init; }

    public ServiceRecoveryDiagnostics? ServiceRecovery { get; init; }
}

public static class AppSettingsRuntimeApplicationDisposition
{
    public const string CommittedAndApplied = "committedAndApplied";
    public const string CommittedWithDeliveryFailures =
        "committedWithDeliveryFailures";
    public const string CommittedWithCapabilityConstraints =
        "committedWithCapabilityConstraints";

    public static string ResolvePublished(
        bool hasDeliveryFailures,
        IReadOnlyList<string> constrainedPaths)
    {
        ArgumentNullException.ThrowIfNull(constrainedPaths);
        if (hasDeliveryFailures)
        {
            return CommittedWithDeliveryFailures;
        }

        return constrainedPaths.Count > 0
            ? CommittedWithCapabilityConstraints
            : CommittedAndApplied;
    }
}
