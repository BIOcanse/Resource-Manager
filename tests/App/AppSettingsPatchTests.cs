using System.Text.Json;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class AppSettingsPatchTests
{
    [Fact]
    public void SmartRecordHistoryDefaultsOffAndCanBeToggledByNormalPatch()
    {
        var defaults = AppSettingsDefaults.Create();
        Assert.False(defaults.Performance.KeepSmartRecordHistory);
        using var enable = JsonDocument.Parse("""{"performance":{"keepSmartRecordHistory":true}}""");
        using var disable = JsonDocument.Parse("""{"performance":{"keepSmartRecordHistory":false}}""");
        var enabled = AppSettingsPatchApplier.Apply(defaults, enable.RootElement);
        Assert.True(enabled.Performance.KeepSmartRecordHistory);
        Assert.True(AppSettingsNormalizer.Normalize(enabled).Performance.KeepSmartRecordHistory);
        Assert.False(AppSettingsPatchApplier.Apply(enabled, disable.RootElement).Performance.KeepSmartRecordHistory);
        Assert.Equal(defaults.Performance.OptimizationMode, enabled.Performance.OptimizationMode);
    }

    [Fact]
    public void Apply_RoundTripsRealtimeActionBudgetAndPreservesBothValuesWhenSwitchingMode()
    {
        var current = AppSettingsDefaults.Create();
        using var percent = JsonDocument.Parse(
            """{"performance":{"realtimeActionBudgetMode":"percent","realtimeActionBudgetCount":64,"realtimeActionBudgetPercent":25}}""");
        using var fixedMode = JsonDocument.Parse("""{"performance":{"realtimeActionBudgetMode":"fixed"}}""");
        using var invalidValues = JsonDocument.Parse(
            """{"performance":{"realtimeActionBudgetCount":10000,"realtimeActionBudgetPercent":0}}""");

        var patched = AppSettingsPatchApplier.Apply(current, percent.RootElement);
        var switched = AppSettingsPatchApplier.Apply(patched, fixedMode.RootElement);
        var clamped = AppSettingsPatchApplier.Apply(switched, invalidValues.RootElement);
        var persisted = JsonSerializer.SerializeToElement(patched, JsonSerializerOptions.Web)
            .GetProperty("performance");

        Assert.Equal(AppRealtimeActionBudgetModes.Percent, patched.Performance.RealtimeActionBudgetMode);
        Assert.Equal("percent", persisted.GetProperty("realtimeActionBudgetMode").GetString());
        Assert.Equal(64, persisted.GetProperty("realtimeActionBudgetCount").GetInt32());
        Assert.Equal(25, persisted.GetProperty("realtimeActionBudgetPercent").GetInt32());
        Assert.Equal(AppRealtimeActionBudgetModes.Fixed, switched.Performance.RealtimeActionBudgetMode);
        Assert.Equal(64, switched.Performance.RealtimeActionBudgetCount);
        Assert.Equal(25, switched.Performance.RealtimeActionBudgetPercent);
        Assert.Equal(8192, clamped.Performance.RealtimeActionBudgetCount);
        Assert.Equal(1, clamped.Performance.RealtimeActionBudgetPercent);
        Assert.Equal(current.Performance.OptimizationMode, patched.Performance.OptimizationMode);
    }

    [Fact]
    public void Apply_ChangesOnlyAddressedLeaf()
    {
        var current = AppSettingsDefaults.Create();
        using var changes = JsonDocument.Parse(
            """
            {
              "debug": {
                "debugLogEnabled": true
              }
            }
            """);

        var patched = AppSettingsPatchApplier.Apply(current, changes.RootElement);

        Assert.True(patched.Debug.DebugLogEnabled);
        Assert.Equal(current.Debug.DebugModeEnabled, patched.Debug.DebugModeEnabled);
        Assert.Equal(
            JsonSerializer.Serialize(current.Performance),
            JsonSerializer.Serialize(patched.Performance));
        Assert.Equal(
            JsonSerializer.Serialize(current.SystemIntegration),
            JsonSerializer.Serialize(patched.SystemIntegration));
    }

    [Theory]
    [InlineData("{\"version\":\"0.0.0\"}")]
    [InlineData("{\"appearance\":{\"unknownField\":true}}")]
    [InlineData("{\"appearance\":{\"theme\":null}}")]
    public void Apply_RejectsUnsafeOrUnknownChanges(string payload)
    {
        using var changes = JsonDocument.Parse(payload);

        Assert.Throws<AppSettingsPatchException>(() =>
            AppSettingsPatchApplier.Apply(
                AppSettingsDefaults.Create(),
                changes.RootElement));
    }
}
