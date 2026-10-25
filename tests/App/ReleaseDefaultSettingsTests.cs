using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class ReleaseDefaultSettingsTests
{
    [Fact]
    public void ReleaseDefaults_IncludeRealtimeActionBudgetAndNeedNoMigration()
    {
        var defaults = AppSettingsDefaults.Create();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(defaults, JsonSerializerOptions.Web));
        var performance = document.RootElement.GetProperty("performance");

        Assert.Equal(AppSettingsDefaults.CurrentVersion, document.RootElement.GetProperty("version").GetString());
        Assert.Equal(AppRealtimeActionBudgetModes.Fixed, performance.GetProperty("realtimeActionBudgetMode").GetString());
        Assert.Equal(32, performance.GetProperty("realtimeActionBudgetCount").GetInt32());
        Assert.Equal(5, performance.GetProperty("realtimeActionBudgetPercent").GetInt32());
        Assert.False(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Theory]
    [InlineData("enabled", "enabled")]
    [InlineData("disabled", "disabled")]
    [InlineData("auto", "enabled")]
    [InlineData("invalid", "enabled")]
    public void OldSettingsDropDeadKeysAndPreserveEffectiveChoices(string mode, string expected)
    {
        var root = JsonSerializer.SerializeToNode(AppSettingsDefaults.Create(), JsonSerializerOptions.Web)!;
        root["version"] = "1.0.26";
        var performance = root["performance"]!.AsObject();
        performance["smartMonitoringMode"] = mode;
        performance["smartMonitoringEnabled"] = false;
        performance["monitoringIdleSeconds"] = 60;
        performance["pauseFrontendRefreshWhenHiddenInNormalMode"] = false;
        performance["frontendHiddenRefreshMode"] = "continueWhenHidden";
        foreach (var key in new[] { "managementRefreshIntervalMs", "discoveryRefreshIntervalMs", "optimizationRefreshIntervalMs" })
            performance[key] = new JsonObject { ["mode"] = "custom", ["customValue"] = 9999 };
        root["appearance"] = new JsonObject { ["resourceBarHardwareAccelerationEnabled"] = false };
        root["appearance"]!["resourceBarHardwareAccelerationMode"] = "disabled";
        performance["preciseGpuPlacementEnabled"] = true;
        performance["gpuOverflowUsagePercent"] = 23;
        performance["gpuOverflowDedicatedMemoryPercent"] = 31;
        using var document = JsonDocument.Parse(root.ToJsonString());

        var migrated = AppSettingsMigrator.MigrateForRead(document.RootElement);
        Assert.False(JsonSerializer.SerializeToElement(migrated.Performance, JsonSerializerOptions.Web).TryGetProperty("smartMonitoringMode", out _));
        Assert.True(migrated.Performance.PreciseGpuPlacementEnabled);
        Assert.Equal(23, migrated.Performance.GpuOverflowUsagePercent);
        Assert.Equal(31, migrated.Performance.GpuOverflowDedicatedMemoryPercent);
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
        var output = JsonSerializer.SerializeToNode(migrated, JsonSerializerOptions.Web)!;
        foreach (var key in new[] { "smartMonitoringEnabled", "monitoringIdleSeconds", "pauseFrontendRefreshWhenHiddenInNormalMode",
                     "frontendHiddenRefreshMode", "managementRefreshIntervalMs", "discoveryRefreshIntervalMs", "optimizationRefreshIntervalMs" })
            Assert.False(output["performance"]!.AsObject().ContainsKey(key));
        using var current = JsonDocument.Parse(output.ToJsonString());
        Assert.False(AppSettingsMigrator.RequiresRewrite(current.RootElement));
    }

    [Fact]
    public void DefaultGpuPlacementIsOffAndScoreOnlyRemainsIndependent()
    {
        var defaults = AppSettingsDefaults.Create();
        Assert.False(defaults.Performance.PreciseGpuPlacementEnabled);
        var normalized = AppSettingsNormalizer.Normalize(defaults with
        {
            Debug = defaults.Debug with { DebugModeEnabled = false, HostManagerSmartCoordinatorScoreOnlyEnabled = true }
        });
        Assert.True(normalized.Debug.HostManagerSmartCoordinatorScoreOnlyEnabled);
    }
}
