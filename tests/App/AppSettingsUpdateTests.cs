using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class AppSettingsUpdateTests
{
    [Theory]
    [InlineData(true, 6, "automatic", true, 24)]
    [InlineData(true, 72, "automatic", true, 72)]
    [InlineData(false, 200, "off", false, 168)]
    public void LegacyBooleanPreservesBehavior(bool enabled, int interval, string mode, bool periodic, int normalizedInterval)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new {
            version = "1.0.32", update = new { autoUpdateEnabled = enabled, checkIntervalHours = interval } }));
        var migrated = AppSettingsMigrator.MigrateForRead(document.RootElement);
        Assert.Equal(mode, migrated.Update!.Mode);
        Assert.Equal(periodic, migrated.Update.PeriodicCheckEnabled);
        Assert.Equal(normalizedInterval, migrated.Update.CheckIntervalHours);
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(migrated, JsonSerializerOptions.Web));
        var update = saved.RootElement.GetProperty("update");
        Assert.Equal(mode, update.GetProperty("mode").GetString());
        Assert.Equal(periodic, update.GetProperty("periodicCheckEnabled").GetBoolean());
        Assert.False(update.TryGetProperty("autoUpdateEnabled", out _));
        Assert.False(AppSettingsMigrator.RequiresRewrite(saved.RootElement));
    }

    [Fact]
    public void MissingOldKeyUsesNewDefaults()
    {
        using var document = JsonDocument.Parse("""{"version":"1.0.32","update":{}}""");
        Assert.Equal(new AppUpdateSettings("automatic", false, 24), AppSettingsMigrator.MigrateForRead(document.RootElement).Update);
        Assert.Equal(new AppUpdateSettings("automatic", false, 24), AppSettingsDefaults.Create().Update);
    }

    [Fact]
    public void InvalidModeUsesExistingEnumNormalizationAndRequiresRewrite()
    {
        var malformed = JsonSerializer.SerializeToNode(AppSettingsDefaults.Create(), JsonSerializerOptions.Web)!;
        malformed["update"]!["mode"] = "other";
        using var document = JsonDocument.Parse(malformed.ToJsonString());
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
        Assert.Equal("automatic", AppSettingsMigrator.MigrateForRead(document.RootElement).Update!.Mode);
        using var patch = JsonDocument.Parse("""{"update":{"mode":"notify","periodicCheckEnabled":true,"checkIntervalHours":1}}""");
        Assert.Equal(new AppUpdateSettings("notify", true, 24), AppSettingsPatchApplier.Apply(AppSettingsDefaults.Create(), patch.RootElement).Update);
        using var invalid = JsonDocument.Parse("""{"update":{"mode":"other"}}""");
        Assert.Equal("automatic", AppSettingsPatchApplier.Apply(AppSettingsDefaults.Create(), invalid.RootElement).Update!.Mode);
        using var retired = JsonDocument.Parse("""{"update":{"autoUpdateEnabled":true}}""");
        Assert.Throws<AppSettingsPatchException>(() => AppSettingsPatchApplier.Apply(AppSettingsDefaults.Create(), retired.RootElement));
    }
}
