
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
    /// <summary>Toggles the performance overlay of the backend's current foreground software
    /// (POST /api/performance-overlay/toggle without a software id). Not destructive.</summary>
    public const string TogglePerformanceOverlayForeground = "toggle-performance-overlay-foreground";
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
    HostMemoryProtectionSettings? MemoryProtection = null,
    MemoryCpuTierSettings? MemoryCpuTier = null,
    AppTargetedRecordingSettings? TargetedRecording = null,
    AppPerformanceOverlaySettings? PerformanceOverlay = null,
    CpuMonopolyProtectionSettings? CpuMonopolyProtection = null,
    bool NormalActionsAllowed = true);

/// <summary>CPU monopoly prevention. Uses memoryProtection.protectedProcesses as its process whitelist.</summary>
public sealed record CpuMonopolyProtectionSettings(
    bool Enabled = false,
    double ThresholdPercent = CpuMonopolyProtectionSettings.DefaultThresholdPercent,
    double SustainedSeconds = CpuMonopolyProtectionSettings.DefaultSustainedSeconds,
    double CheckIntervalSeconds = CpuMonopolyProtectionSettings.DefaultCheckIntervalSeconds,
    int RecordCapacity = CpuMonopolyProtectionSettings.DefaultRecordCapacity,
    int ProcessCapacity = CpuMonopolyProtectionSettings.DefaultProcessCapacity,
    int SubscriberCapacity = CpuMonopolyProtectionSettings.DefaultSubscriberCapacity,
    double MaximumSampleGapSeconds = CpuMonopolyProtectionSettings.DefaultMaximumSampleGapSeconds)
{
    public const double DefaultThresholdPercent = 90;
    public const double DefaultSustainedSeconds = 10;
    public const double DefaultCheckIntervalSeconds = 1;
    public const double DefaultMaximumSampleGapSeconds = 3;
    public const int DefaultRecordCapacity = 4096;
    public const int DefaultProcessCapacity = 4096;
    public const int DefaultSubscriberCapacity = 64;
    public const double MinimumThresholdPercent = double.Epsilon;
    public const double MaximumThresholdPercent = 100;
    public const double MinimumSeconds = 1;
    public const double MaximumSeconds = 86400;
    public const int MinimumCapacity = 1;
    public const int MaximumRecordCapacity = 16384;
    public const int MaximumProcessCapacity = 16384;
    public const int MaximumSubscriberCapacity = 4096;
}

/// <summary>内存与 CPU 取舍: the chosen tier (1 = least memory, most CPU; 5 = least CPU, most memory)
/// and the user's per-option values by option name. An option without an entry takes the tier's value;
/// removing an entry returns that option to the tier. Changing the tier keeps the entries. A null entry
/// value means "not set" and is accepted only for options whose range allows that.</summary>
public sealed record MemoryCpuTierSettings(
    int Tier = MemoryCpuTierSettings.DefaultTier,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Overrides = null)
{
    public const int MinimumTier = 1;
    public const int DefaultTier = 3;
    public const int MaximumTier = 5;
}

public sealed record HostMemoryProtectionSettings(
    bool Enabled = false,
    double ThresholdPercent = 95,
    double CheckIntervalSeconds = 1,
    IReadOnlyList<HostMemoryReserveSettings>? Reserves = null,
    string ActionMode = HostMemoryProtectionActionModes.DumpThenTerminate,
    long MinimumProcessGrowthBytes = 1073741824)
{
    public IReadOnlyList<string> ProtectedProcesses { get; init; } = ["explorer.exe"];
}

public static class HostMemoryProtectionActionModes
{
    public const string DumpThenTerminate = "dumpThenTerminate";
    public const string TerminateOnly = "terminateOnly";
    public static bool IsSupported(string? value) => value is DumpThenTerminate or TerminateOnly;
}

public sealed record HostMemoryReserveSettings(string Location, ulong SizeBytes);

public sealed record AppHardwareControlSettings(int ReassertionIntervalSeconds = 120, bool KeepAlive = false);

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
    int RealtimeActionBudgetPercent)
{
    public bool KeepSmartRecordHistory { get; init; } = false;
}

public sealed record AppSystemIntegrationSettings(
    bool TaskManagerShortcutReplacementEnabled,
    IReadOnlyList<AppEditableHotkeySettings> Hotkeys,
    bool AutoStartEnabled = false,
    bool RestartAfterCrashEnabled = true,
    int CrashRestartDelaySeconds = 10,
    int CrashRestartCount = 3)
{
    // 崩溃转储 (docs/crash_dumps_2026-10-31.md). An older settings file without it reads the defaults.
    public AppCrashDumpSettings CrashDumps { get; init; } = new();
    public AppCrashDumpKeySettings CrashDumpKeys { get; init; } = new();
    public AppDeviceRestartSettings DeviceRestart { get; init; } = new();
}

/// <summary>崩溃转储: <see cref="Mode"/> <c>default</c> follows the release channel (backend freedom point
/// <c>service_recovery.crash_reports</c>: beta resolves to on, stable to ask); <c>ask</c> writes no dumps and
/// the frontend shows the request card; <c>on</c> writes WER LocalDumps keys for RM's images; <c>off</c> removes them.
/// <see cref="KeepCount"/> is the per-program count of dumps and crash records kept.</summary>
public sealed record AppCrashDumpSettings(
    string Mode = AppCrashDumpModes.Default,
    string DumpType = AppCrashDumpTypes.Mini,
    int KeepCount = AppCrashDumpSettings.DefaultKeepCount)
{
    public const int DefaultKeepCount = 5;
}

public static class AppCrashDumpModes
{
    public const string Default = "default";
    public const string Ask = "ask";
    public const string On = "on";
    public const string Off = "off";

    public static bool IsSupported(string? mode) => mode is Default or Ask or On or Off;

    // The values a channel default may resolve to.
    public static bool IsResolved(string? mode) => mode is Ask or On or Off;
}

public static class AppCrashDumpTypes
{
    public const string Mini = "mini";
    public const string Full = "full";

    public static bool IsSupported(string? type) => type is Mini or Full;
}

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

    public string? RuntimeFailurePath { get; init; }

    public ServiceRecoveryDiagnostics? ServiceRecovery { get; init; }

    public CrashDumpDiagnostics? CrashDumps { get; init; }
    public CrashDumpKeyDiagnostics? CrashDumpKeys { get; init; }
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
