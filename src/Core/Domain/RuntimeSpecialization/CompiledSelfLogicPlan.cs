using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledSelfLogicPlan(
    AppPresetNumericSetting MonitorRefreshIntervalMs,
    AppPresetNumericSetting ResourceTableRefreshIntervalMs,
    AppPresetNumericSetting ManagementRefreshIntervalMs,
    AppPresetNumericSetting DiscoveryRefreshIntervalMs,
    AppPresetNumericSetting OptimizationRefreshIntervalMs,
    AppPresetNumericSetting LocalSystemRefreshIntervalMs)
{
    public static CompiledSelfLogicPlan Default { get; } = new(
        new AppPresetNumericSetting(
            AppPresetNumericSettingModes.Aotu,
            AppLogicRefreshIntervalPresets.Responsive,
            1_000),
        new AppPresetNumericSetting(
            AppPresetNumericSettingModes.Aotu,
            AppLogicRefreshIntervalPresets.Responsive,
            1_000),
        new AppPresetNumericSetting(
            AppPresetNumericSettingModes.Aotu,
            AppLogicRefreshIntervalPresets.Balanced,
            10_000),
        new AppPresetNumericSetting(
            AppPresetNumericSettingModes.Aotu,
            AppLogicRefreshIntervalPresets.Responsive,
            3_000),
        new AppPresetNumericSetting(
            AppPresetNumericSettingModes.Aotu,
            AppLogicRefreshIntervalPresets.Balanced,
            10_000),
        new AppPresetNumericSetting(
            AppPresetNumericSettingModes.Aotu,
            AppLogicRefreshIntervalPresets.Balanced,
            60_000));
}
