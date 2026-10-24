using ResourceManager.App.Domain.Units;

namespace ResourceManager.App.Domain.Settings;

public static class AppAnimationModes
{
    public const string Auto = "auto";
    public const string None = "none";
    public const string Normal = "normal";
    public const string Ultra = "ultra";
}

public static class AppAdaptiveBooleanModes
{
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
}

// 资源条过渡动画的帧率：display = 跟随屏幕刷新（每个动画帧都画），custom = 按 ResourceBarFrameRate 限帧。
public static class AppResourceBarFrameRateModes
{
    public const string Display = "display";
    public const string Custom = "custom";
}

public static class AppPresetNumericSettingModes
{
    public const string Aotu = "aotu";
    public const string Preset = "preset";
    public const string Custom = "custom";
}

public static class AppLogicRefreshIntervalPresets
{
    public const string Responsive = "responsive";
    public const string Balanced = "balanced";
    public const string LowPower = "lowPower";
    public const string Quiet = "quiet";

    public static readonly ISet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Responsive,
        Balanced,
        LowPower,
        Quiet
    };
}

public static class AppBarColorModes
{
    public const string Type = "type";
    public const string Distinct = "distinct";
}

// 读数来源怎么列：preferred = 每个读取项只列本机首选来源（默认），all = 列出所有可用来源并标出首选。
// 后端只存和校验这个值；首选由后端核定，按它筛选和加标记在前端（src/UI/Frontend/src/presentation/readingSources.ts）。
// 调度不读这个设置，始终用后端核定的首选。
public static class AppReadingSourceModes
{
    public const string Preferred = "preferred";
    public const string All = "all";
}

public static class AppFontSmoothingModes
{
    public const string Auto = "auto";
    public const string System = "system";
    public const string Grayscale = "grayscale";
    public const string Disabled = "disabled";
}

public static class AppLanguageModes
{
    public const string System = "system";
    public const string SimplifiedChinese = "zh-CN";
    public const string TraditionalChinese = "zh-TW";
    public const string English = "en-US";
    public const string Japanese = "ja-JP";
    public const string Korean = "ko-KR";
    public const string French = "fr-FR";
    public const string German = "de-DE";
    public const string Spanish = "es-ES";
    public const string PortugueseBrazil = "pt-BR";
    public const string Russian = "ru-RU";
    public const string Italian = "it-IT";
    public const string Arabic = "ar-SA";

    public static readonly ISet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        System,
        SimplifiedChinese,
        TraditionalChinese,
        English,
        Japanese,
        Korean,
        French,
        German,
        Spanish,
        "es-MX",
        PortugueseBrazil,
        "pt-PT",
        Russian,
        Ukrainian,
        Polish,
        Turkish,
        Italian,
        Dutch,
        Swedish,
        Finnish,
        Danish,
        Norwegian,
        Czech,
        Hungarian,
        Romanian,
        Greek,
        Hebrew,
        Arabic,
        Hindi,
        Indonesian,
        Vietnamese,
        Thai
    };

    public const string Ukrainian = "uk-UA";
    public const string Polish = "pl-PL";
    public const string Turkish = "tr-TR";
    public const string Dutch = "nl-NL";
    public const string Swedish = "sv-SE";
    public const string Finnish = "fi-FI";
    public const string Danish = "da-DK";
    public const string Norwegian = "nb-NO";
    public const string Czech = "cs-CZ";
    public const string Hungarian = "hu-HU";
    public const string Romanian = "ro-RO";
    public const string Greek = "el-GR";
    public const string Hebrew = "he-IL";
    public const string Hindi = "hi-IN";
    public const string Indonesian = "id-ID";
    public const string Vietnamese = "vi-VN";
    public const string Thai = "th-TH";
}

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

public static class AppGpuPerformanceUseCases
{
    public const string General = "general";
    public const string Ai = "ai";
    public const string Gaming = "gaming";

    public static readonly ISet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        General,
        Ai,
        Gaming
    };
}

public sealed record AppSettings(
    string Version,
    AppPerformanceSettings Performance,
    AppAppearanceSettings Appearance,
    AppSystemIntegrationSettings SystemIntegration,
    AppDebugSettings Debug,
    AppLocalPublicServiceSettings PublicService,
    AppAiModelServiceSettings AiModelService,
    AppUpdateSettings? Update = null,
    AppHardwareControlSettings? Control = null,
    AppReportSettings? Reports = null);

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

public sealed record AppUpdateSettings(bool AutoUpdateEnabled);

public sealed record AppPresetNumericSetting(
    string Mode,
    string Preset,
    int CustomValue);

public sealed record AppPerformanceSettings(
    string OptimizationMode,
    bool PreciseGpuPlacementEnabled,
    byte GpuOverflowUsagePercent,
    byte GpuOverflowDedicatedMemoryPercent,
    byte PhysicalMemoryAutomaticCleanupPercent,
    byte VirtualMemoryAutomaticCleanupPercent,
    byte PhysicalMemoryOptimizationTargetUsagePercent,
    byte VirtualMemoryOptimizationTargetUsagePercent,
    IReadOnlyList<string> GpuPerformanceUseCases,
    string SmartMonitoringMode,
    AppPresetNumericSetting MonitorRefreshIntervalMs,
    AppPresetNumericSetting ResourceTableRefreshIntervalMs,
    AppPresetNumericSetting LocalSystemRefreshIntervalMs);

// UiStyle 是界面风格，Theme 是该风格下的配色；合法组合见 src/Shared/Appearance/ui-styles.json。
public sealed record AppAppearanceSettings(
    string UiStyle,
    string Theme,
    string Animations,
    // 见 AppResourceBarFrameRateModes；ResourceBarFrameRate 是 custom 时的帧/秒，
    // 范围 AppSettingsDefaults.MinResourceBarFrameRate–MaxResourceBarFrameRate，display 时保留不用。
    string ResourceBarFrameRateMode,
    int ResourceBarFrameRate,
    string BarColorMode,
    string FontSmoothing,
    string Language,
    // 容量数字用哪个进制显示，见 Domain/Units/AppByteUnitModes.cs。
    // native = 每个量按它本来的进制（内存类 1024、存储类 1000）；
    // binary = 一律 1024；decimal = 一律 1000。
    // 后端只存和校验这个值，不按它做任何换算 —— 换算在前端。
    string ByteUnitMode,
    // 见 AppReadingSourceModes。
    string ReadingSourceMode);

public sealed record AppSystemIntegrationSettings(
    bool TaskManagerShortcutReplacementEnabled,
    IReadOnlyList<AppEditableHotkeySettings> Hotkeys,
    bool AutoStartEnabled = false);

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

    public AppFrontendRefreshIntervals FrontendRefreshIntervals =>
        AppFrontendRefreshIntervalPolicy.Expand(Settings.Performance);

    public string RuntimeApplicationDisposition { get; init; } = "notRequested";

    public long? RuntimePlanVersion { get; init; }

    public ulong? RuntimePublicationSequence { get; init; }

    public int RuntimeDeliveryFailureCount { get; init; }

    public IReadOnlyList<string> RuntimeCapabilityConstrainedPaths { get; init; } = [];

    public string? RuntimeFailureCode { get; init; }
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
