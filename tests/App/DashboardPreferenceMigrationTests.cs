using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Settings;
using ResourceManager.NativeUi.Preferences;

namespace Resource_Manager_APP.Tests;

public sealed class DashboardPreferenceMigrationTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public async Task HeadWriterLayoutSurvivesEitherStartupOrderAndExistingPhaseACache(int order, bool phaseACache)
    {
        using var fixture = new Fixture();
        var original = await File.ReadAllBytesAsync(BackendPreferenceBoundaryTests.Fixture("dashboard-settings.json"));
        await File.WriteAllBytesAsync(fixture.Settings, original);
        if (phaseACache)
        {
            var preferences = new FrontendPreferences { Appearance = new() { Language = "ja-JP" } };
            await File.WriteAllTextAsync(fixture.Cache, JsonSerializer.Serialize(preferences, FrontendPreferencesOwner.Json));
            await File.WriteAllTextAsync(fixture.Cache + ".imported-v1", "frontend-preferences-v1");
        }
        var owner = fixture.Owner();
        var migration = fixture.Migration();
        if (order == 1) owner.Read();
        if (order == 2) await Task.WhenAll(migration.RunAsync(default), Task.Run(() => owner.Read()));
        else await migration.RunAsync(default);

        var imported = owner.Read();
        var layout = imported.Dashboard!.Value;
        Assert.Equal("custom-memory", layout.GetProperty("cards")[0].GetProperty("id").GetString());
        Assert.Equal("network.send", layout.GetProperty("resourceBars")[0].GetProperty("metricId").GetString());
        var name = layout.GetProperty("resourceTableColumns").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "name");
        Assert.Equal(311, name.GetProperty("width").GetDouble());
        Assert.False(layout.GetProperty("resourceTableColumns").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "memory").GetProperty("visible").GetBoolean());
        Assert.Equal(phaseACache ? "ja-JP" : FrontendPoints.Language, imported.Appearance.Language);
        Assert.Equal(original, await Read(fixture.Settings + PresentationPreferenceRemoval.DashboardBackupSuffix));
        var retired = await Read(fixture.Settings);
        using (var document = JsonDocument.Parse(retired))
        {
            Assert.Equal(17, document.RootElement.GetProperty("version").GetInt32());
            Assert.Single(document.RootElement.EnumerateObject());
            Assert.False(PresentationPreferenceRemoval.HasDashboardFields(document.RootElement));
        }
        var backupTime = File.GetLastWriteTimeUtc(fixture.Settings + PresentationPreferenceRemoval.DashboardBackupSuffix);
        await migration.RunAsync(default);
        Assert.Equal(retired, await Read(fixture.Settings));
        Assert.Equal(backupTime, File.GetLastWriteTimeUtc(fixture.Settings + PresentationPreferenceRemoval.DashboardBackupSuffix));

        var node = JsonNode.Parse(layout.GetRawText())!.AsObject();
        node["cards"] = new JsonArray();
        owner.SaveDashboard(imported with { Dashboard = JsonSerializer.SerializeToElement(node) });
        Assert.Empty(fixture.Owner().Read().Dashboard!.Value.GetProperty("cards").EnumerateArray());
        owner.SaveGlobal(imported with { Appearance = imported.Appearance with { Language = "en-US" } });
        Assert.Empty(fixture.Owner().Read().Dashboard!.Value.GetProperty("cards").EnumerateArray());
        owner.Reset();
        Assert.Null(fixture.Owner().Read().Dashboard);
        Assert.True(File.Exists(fixture.Cache + ".dashboard-imported-v1"));
    }

    [Fact]
    public async Task LastGoodImportIsExplicitAndItsOriginalBytesArePreserved()
    {
        using var fixture = new Fixture();
        var original = await File.ReadAllBytesAsync(BackendPreferenceBoundaryTests.Fixture("dashboard-settings.json"));
        await File.WriteAllTextAsync(fixture.Settings, "invalid legacy JSON");
        var lastGood = Path.Combine(fixture.Config, "dashboard-settings.last-good.json");
        await File.WriteAllBytesAsync(lastGood, original);
        await fixture.Migration().RunAsync(default);
        Assert.Equal(original, await Read(lastGood + PresentationPreferenceRemoval.DashboardBackupSuffix));
        Assert.Equal("custom-memory", fixture.Owner().Read().Dashboard!.Value.GetProperty("cards")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ExistingDashboardCacheWinsAndReadOnlyBackendNeverRetiresFiles()
    {
        using var fixture = new Fixture();
        var original = await File.ReadAllBytesAsync(BackendPreferenceBoundaryTests.Fixture("dashboard-settings.json"));
        await File.WriteAllBytesAsync(fixture.Settings, original);
        await fixture.Migration(false).RunAsync(default);
        Assert.Equal(original, await Read(fixture.Settings));
        Assert.False(File.Exists(fixture.Settings + PresentationPreferenceRemoval.DashboardBackupSuffix));
        var owner = fixture.Owner();
        var preferences = owner.Read();
        var node = JsonNode.Parse(preferences.Dashboard!.Value.GetRawText())!;
        node["cards"] = new JsonArray();
        owner.SaveDashboard(preferences with { Dashboard = JsonSerializer.SerializeToElement(node) });
        File.Delete(fixture.Cache + ".dashboard-imported-v1"); // interrupted marker commit
        Assert.Empty(fixture.Owner().Read().Dashboard!.Value.GetProperty("cards").EnumerateArray());
    }

    [Fact]
    public void BackendHasNoLayoutTypesAndProviderPlanHasNoScreenSelections()
    {
        var backend = typeof(CompiledMonitoringPlan).Assembly;
        foreach (var name in new[] { "Domain.Settings.DashboardSettings", "Application.Settings.DashboardSettingsDefaults",
            "Application.Settings.DashboardSettingsNormalizer", "Application.Settings.IDashboardSettingsStore",
            "Infrastructure.Settings.JsonDashboardSettingsStore", "Infrastructure.RuntimeSpecialization.DashboardMonitoringPlanSynchronizer" })
            Assert.Null(backend.GetType("ResourceManager.App." + name));
        Assert.Equal(new[] { "FirmwareProviders", "RegisteredSourceZoneIds" }, typeof(CompiledMonitoringPlan).GetProperties()
            .Where(property => property.GetMethod?.IsStatic == false).Select(property => property.Name).Order());
    }

    private static Task<byte[]> Read(string path) => WindowsNativeAtomicFileCommitter.ReadCommittedBytesAsync(path, default);

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "rm-dashboard-import-" + Guid.NewGuid().ToString("N"));
        private readonly string application;
        internal string Config { get; }
        internal string Settings => Path.Combine(Config, "dashboard-settings.json");
        internal string Cache => Path.Combine(Config, "frontend.cache.json");
        internal Fixture()
        {
            application = Directory.CreateDirectory(Path.Combine(root, "APP")).FullName;
            Config = Directory.CreateDirectory(Path.Combine(root, "Config")).FullName;
        }
        internal FrontendPreferencesOwner Owner() => new(Cache, legacyDashboardPath: Settings);
        internal LegacyDashboardFileMigration Migration(bool writable = true) => new(new Environment(application), new RuntimePersistenceCapabilityPolicy(writable));
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Config)) File.Delete(file);
            Directory.Delete(Config); Directory.Delete(application); Directory.Delete(root);
        }
    }
    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
