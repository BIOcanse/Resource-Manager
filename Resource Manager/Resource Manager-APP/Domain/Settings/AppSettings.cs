using ResourceManager.App.Domain.Units;

namespace ResourceManager.App.Domain.Settings;

public static class AppThemeModes
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";
    public const string LowContrast = "lowContrast";
}

public static class AppAnimationModes
{
    public const string Auto = "auto";
    public const string None = "none";
    public const string Normal = "normal";
    public const string Ultra = "ultra";
}

public static class AppAdaptiveBooleanModes
{
    public const string Auto = "auto";
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
}

public static class AppFrontendHiddenRefreshModes
{
    public const string Auto = "auto";
    public const string PauseWhenHidden = "pauseWhenHidden";
    public const string ContinueWhenHidden = "continueWhenHidden";
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
    public const string Smart = "smart";
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
    AppAiModelServiceSettings AiModelService);

public sealed record AppPresetNumericSetting(
    string Mode,
    string Preset,
    int CustomValue);

public sealed record AppPerformanceSettings(
    bool SmartMonitoringEnabled,
    int MonitoringIdleSeconds,
    string OptimizationMode,
    bool PauseFrontendRefreshWhenHiddenInNormalMode,
    bool PreciseGpuPlacementEnabled,
    byte VramMoveDownPhysicalMemoryDangerPercent,
    byte PhysicalMemoryMoveDownVirtualMemoryDangerPercent,
    byte PhysicalMemoryAutomaticCleanupPercent,
    byte VirtualMemoryAutomaticCleanupPercent,
    byte PhysicalMemoryOptimizationTargetUsagePercent,
    byte VirtualMemoryOptimizationTargetUsagePercent,
    IReadOnlyList<string> GpuPerformanceUseCases,
    string SmartMonitoringMode,
    string FrontendHiddenRefreshMode,
    // 开启后，处于自动调度模式的机制可以按本机事实做额外优化
    // （例如只有一个显卡时不再跑 GPU 调度）。关闭则一律按用户设置照常运行。
    bool AutomaticSchedulingOptimizationsEnabled,
    AppPresetNumericSetting MonitorRefreshIntervalMs,
    AppPresetNumericSetting ResourceTableRefreshIntervalMs,
    AppPresetNumericSetting ManagementRefreshIntervalMs,
    AppPresetNumericSetting DiscoveryRefreshIntervalMs,
    AppPresetNumericSetting OptimizationRefreshIntervalMs,
    AppPresetNumericSetting LocalSystemRefreshIntervalMs);

public sealed record AppAppearanceSettings(
    string Theme,
    string Animations,
    bool ResourceBarHardwareAccelerationEnabled,
    string ResourceBarHardwareAccelerationMode,
    string BarColorMode,
    string FontSmoothing,
    string Language,
    // 容量数字用哪个进制显示，见 Domain/Units/AppByteUnitModes.cs。
    // native = 每个量按它本来的进制（内存类 1024、存储类 1000）；
    // binary = 一律 1024；decimal = 一律 1000。
    // 后端只存和校验这个值，不按它做任何换算 —— 换算在前端。
    string ByteUnitMode);

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
