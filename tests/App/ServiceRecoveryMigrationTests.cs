using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class ServiceRecoveryMigrationTests
{
    [Fact]
    public void Version1030MigratesToEnabledDefaults()
    {
        var node = JsonSerializer.SerializeToNode(AppSettingsDefaults.Create(), JsonSerializerOptions.Web)!;
        node["version"] = "1.0.30";
        var section = node["systemIntegration"]!.AsObject();
        foreach (var key in new[] { "restartAfterCrashEnabled", "crashRestartDelaySeconds", "crashRestartCount" })
            section.Remove(key);
        using var document = JsonDocument.Parse(node.ToJsonString());
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
        var migrated = AppSettingsMigrator.MigrateForRead(document.RootElement);
        Assert.Equal(AppSettingsDefaults.CurrentVersion, migrated.Version);
        Assert.True(migrated.SystemIntegration.RestartAfterCrashEnabled);
        Assert.Equal(10, migrated.SystemIntegration.CrashRestartDelaySeconds);
        Assert.Equal(3, migrated.SystemIntegration.CrashRestartCount);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(9999, 99, 3600, 10)]
    [InlineData(42, 7, 42, 7)]
    public void LoadValidationAndRoundTripPreserveBoundedValues(int delay, int count, int expectedDelay, int expectedCount)
    {
        var input = AppSettingsDefaults.Create();
        input = input with { SystemIntegration = input.SystemIntegration with
        { RestartAfterCrashEnabled = false, CrashRestartDelaySeconds = delay, CrashRestartCount = count } };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(input, JsonSerializerOptions.Web));
        var migrated = AppSettingsMigrator.MigrateForRead(document.RootElement);
        Assert.False(migrated.SystemIntegration.RestartAfterCrashEnabled);
        Assert.Equal(expectedDelay, migrated.SystemIntegration.CrashRestartDelaySeconds);
        Assert.Equal(expectedCount, migrated.SystemIntegration.CrashRestartCount);
        Assert.Equal(delay != expectedDelay || count != expectedCount, AppSettingsMigrator.RequiresRewrite(document.RootElement));
        using var roundTrip = JsonDocument.Parse(JsonSerializer.Serialize(migrated, JsonSerializerOptions.Web));
        Assert.False(AppSettingsMigrator.RequiresRewrite(roundTrip.RootElement));
        var reloaded = AppSettingsMigrator.MigrateForRead(roundTrip.RootElement).SystemIntegration;
        Assert.False(reloaded.RestartAfterCrashEnabled);
        Assert.Equal(expectedDelay, reloaded.CrashRestartDelaySeconds);
        Assert.Equal(expectedCount, reloaded.CrashRestartCount);
    }

    [Theory]
    [InlineData("crashRestartDelaySeconds", "\"bad\"")]
    [InlineData("crashRestartCount", "null")]
    [InlineData("restartAfterCrashEnabled", "1")]
    public void MalformedFieldsRequireRewrite(string key, string value)
    {
        var node = JsonSerializer.SerializeToNode(AppSettingsDefaults.Create(), JsonSerializerOptions.Web)!;
        node["systemIntegration"]![key] = JsonNode.Parse(value);
        using var document = JsonDocument.Parse(node.ToJsonString());
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
        var migrated = AppSettingsMigrator.MigrateForRead(document.RootElement);
        Assert.True(migrated.SystemIntegration.RestartAfterCrashEnabled);
        Assert.Equal(10, migrated.SystemIntegration.CrashRestartDelaySeconds);
        Assert.Equal(3, migrated.SystemIntegration.CrashRestartCount);
    }
}
