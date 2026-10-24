namespace ResourceManager.App.Domain.Settings;

public sealed record AppRefreshIntervalValues(
    int Monitor,
    int ResourceTable,
    int LocalSystem);

public sealed record AppRefreshIntervalPresetValues(
    int Responsive,
    int Balanced,
    int LowPower,
    int Quiet);

public sealed record AppRefreshIntervalPresetCatalog(
    AppRefreshIntervalPresetValues Monitor,
    AppRefreshIntervalPresetValues ResourceTable,
    AppRefreshIntervalPresetValues LocalSystem);

public sealed record AppRefreshIntervalDefaults(
    AppPresetNumericSetting Monitor,
    AppPresetNumericSetting ResourceTable,
    AppPresetNumericSetting LocalSystem);

public sealed record AppFrontendRefreshIntervals(
    AppRefreshIntervalValues Foreground,
    AppRefreshIntervalValues BackgroundOptimized,
    AppRefreshIntervalPresetCatalog Presets,
    AppRefreshIntervalDefaults Defaults);

public static class AppFrontendRefreshIntervalPolicy
{
    public const int MinIntervalMs = 500;
    public const int MaxIntervalMs = 300_000;

    public const int MonitorResponsiveIntervalMs = 1_000;
    public const int MonitorBalancedIntervalMs = 2_000;
    public const int MonitorLowPowerIntervalMs = 3_000;
    public const int MonitorQuietIntervalMs = 5_000;

    public const int ResourceTableResponsiveIntervalMs = 1_000;
    public const int ResourceTableBalancedIntervalMs = 2_000;
    public const int ResourceTableLowPowerIntervalMs = 3_000;
    public const int ResourceTableQuietIntervalMs = 5_000;

    public const int LocalSystemResponsiveIntervalMs = 30_000;
    public const int LocalSystemBalancedIntervalMs = 60_000;
    public const int LocalSystemLowPowerIntervalMs = 120_000;
    public const int LocalSystemQuietIntervalMs = 300_000;

    public static AppRefreshIntervalPresetCatalog Presets { get; } = new(
        new AppRefreshIntervalPresetValues(
            MonitorResponsiveIntervalMs,
            MonitorBalancedIntervalMs,
            MonitorLowPowerIntervalMs,
            MonitorQuietIntervalMs),
        new AppRefreshIntervalPresetValues(
            ResourceTableResponsiveIntervalMs,
            ResourceTableBalancedIntervalMs,
            ResourceTableLowPowerIntervalMs,
            ResourceTableQuietIntervalMs),
        new AppRefreshIntervalPresetValues(
            LocalSystemResponsiveIntervalMs,
            LocalSystemBalancedIntervalMs,
            LocalSystemLowPowerIntervalMs,
            LocalSystemQuietIntervalMs));

    public static AppRefreshIntervalDefaults Defaults { get; } = new(
        new AppPresetNumericSetting(
            AppPresetNumericSettingModes.Aotu,
            AppLogicRefreshIntervalPresets.Responsive,
            MonitorResponsiveIntervalMs),
        new AppPresetNumericSetting(
            AppPresetNumericSettingModes.Aotu,
            AppLogicRefreshIntervalPresets.Responsive,
            ResourceTableResponsiveIntervalMs),
        new AppPresetNumericSetting(
            AppPresetNumericSettingModes.Aotu,
            AppLogicRefreshIntervalPresets.Balanced,
            LocalSystemBalancedIntervalMs));

    public static AppFrontendRefreshIntervals Expand(AppPerformanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var monitor = Normalize(
            settings.MonitorRefreshIntervalMs,
            Defaults.Monitor.Preset,
            Defaults.Monitor.CustomValue);
        var resourceTable = Normalize(
            settings.ResourceTableRefreshIntervalMs,
            Defaults.ResourceTable.Preset,
            Defaults.ResourceTable.CustomValue);
        var localSystem = Normalize(
            settings.LocalSystemRefreshIntervalMs,
            Defaults.LocalSystem.Preset,
            Defaults.LocalSystem.CustomValue);

        return new AppFrontendRefreshIntervals(
            new AppRefreshIntervalValues(
                Resolve(monitor, Presets.Monitor, backgroundOptimized: false),
                Resolve(resourceTable, Presets.ResourceTable, backgroundOptimized: false),
                Resolve(localSystem, Presets.LocalSystem, backgroundOptimized: false)),
            new AppRefreshIntervalValues(
                Resolve(monitor, Presets.Monitor, backgroundOptimized: true),
                Resolve(resourceTable, Presets.ResourceTable, backgroundOptimized: true),
                Resolve(localSystem, Presets.LocalSystem, backgroundOptimized: true)),
            Presets,
            Defaults);
    }

    public static AppPresetNumericSetting Normalize(
        AppPresetNumericSetting? setting,
        string fallbackPreset,
        int fallbackCustomValue)
        => new(
            NormalizePresetNumericMode(setting?.Mode),
            NormalizeLogicRefreshIntervalPreset(setting?.Preset, fallbackPreset),
            Math.Clamp(
                setting?.CustomValue ?? fallbackCustomValue,
                MinIntervalMs,
                MaxIntervalMs));

    public static string NormalizePresetNumericMode(string? mode)
        => mode switch
        {
            AppPresetNumericSettingModes.Preset => AppPresetNumericSettingModes.Preset,
            AppPresetNumericSettingModes.Custom => AppPresetNumericSettingModes.Custom,
            _ => AppPresetNumericSettingModes.Aotu
        };

    public static string NormalizeLogicRefreshIntervalPreset(
        string? preset,
        string fallbackPreset)
    {
        var fallback = NormalizeSupportedPreset(fallbackPreset)
            ?? AppLogicRefreshIntervalPresets.Balanced;
        return NormalizeSupportedPreset(preset) ?? fallback;
    }

    private static int Resolve(
        AppPresetNumericSetting setting,
        AppRefreshIntervalPresetValues presets,
        bool backgroundOptimized)
    {
        return setting.Mode switch
        {
            AppPresetNumericSettingModes.Custom => setting.CustomValue,
            AppPresetNumericSettingModes.Preset => ResolvePreset(presets, setting.Preset),
            _ => ResolvePreset(
                presets,
                backgroundOptimized
                    ? AppLogicRefreshIntervalPresets.LowPower
                    : AppLogicRefreshIntervalPresets.Responsive)
        };
    }

    private static int ResolvePreset(
        AppRefreshIntervalPresetValues presets,
        string preset)
        => preset switch
        {
            AppLogicRefreshIntervalPresets.Responsive => presets.Responsive,
            AppLogicRefreshIntervalPresets.Balanced => presets.Balanced,
            AppLogicRefreshIntervalPresets.LowPower => presets.LowPower,
            AppLogicRefreshIntervalPresets.Quiet => presets.Quiet,
            _ => throw new ArgumentOutOfRangeException(nameof(preset))
        };

    private static string? NormalizeSupportedPreset(string? preset)
    {
        var normalized = preset?.Trim();
        if (string.Equals(normalized, AppLogicRefreshIntervalPresets.Responsive, StringComparison.OrdinalIgnoreCase))
        {
            return AppLogicRefreshIntervalPresets.Responsive;
        }

        if (string.Equals(normalized, AppLogicRefreshIntervalPresets.Balanced, StringComparison.OrdinalIgnoreCase))
        {
            return AppLogicRefreshIntervalPresets.Balanced;
        }

        if (string.Equals(normalized, AppLogicRefreshIntervalPresets.LowPower, StringComparison.OrdinalIgnoreCase))
        {
            return AppLogicRefreshIntervalPresets.LowPower;
        }

        if (string.Equals(normalized, AppLogicRefreshIntervalPresets.Quiet, StringComparison.OrdinalIgnoreCase))
        {
            return AppLogicRefreshIntervalPresets.Quiet;
        }

        return null;
    }
}
