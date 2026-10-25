using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Settings;
using ResourceManager.NativeUi.Preferences;

namespace Resource_Manager_APP.Tests;

public sealed class BackendPreferenceBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTwoMetricsOnlyFileMigratesAndImportsWithoutReplacingAnExistingBackup(bool existingBackup)
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-overlay-v2-" + Guid.NewGuid().ToString("N"));
        var application = Directory.CreateDirectory(Path.Combine(root, "APP")).FullName;
        var profiles = Directory.CreateDirectory(Path.Combine(root, "UserData", "SoftwareProfiles")).FullName;
        var path = Path.Combine(profiles, "performance-overlay.local.json");
        var cache = Path.Combine(profiles, "frontend.cache.json");
        try
        {
            // Derive the v2 writer's shape from the recorded legacy writer image:
            // round two removed its six layout fields but retained metrics.
            var image = JsonNode.Parse(await File.ReadAllBytesAsync(Fixture("performance-overlay.local.json")))!;
            image["software"]![0]!["metrics"] = new JsonArray("target.memory", "target.fps");
            var original = JsonSerializer.SerializeToUtf8Bytes(image);
            image["version"] = 2;
            foreach (var field in PresentationPreferenceRemoval.OverlayFields.Where(static field => field != "metrics"))
                image["software"]![0]!.AsObject().Remove(field);
            var v2 = JsonSerializer.SerializeToUtf8Bytes(image);
            await File.WriteAllBytesAsync(path, v2);
            var backup = path + PresentationPreferenceRemoval.BackupSuffix;
            if (existingBackup) await File.WriteAllBytesAsync(backup, original);
            Assert.Equal(v2, await ReadCommitted(path));
            if (existingBackup) Assert.Equal(original, await ReadCommitted(backup));
            var owner = new FrontendPreferencesOwner(cache, null, path);
            Assert.Equal(new[] { "target.memory", "target.fps" }, owner.Read().Overlays["legacy-game"].Metrics);
            Assert.Equal(v2, await ReadCommitted(path));
            owner.Reset();
            Assert.Equal(owner.Read().Overlays["legacy-game"].Enabled, new FrontendPreferencesOwner(cache, null, path).Read().Overlays["legacy-game"].Enabled);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(profiles)) File.Delete(file);
            Directory.Delete(profiles);
            Directory.Delete(Path.Combine(root, "UserData"));
            Directory.Delete(application);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void PreRoundThreeCacheImportsCurrentLegacyOperationsOnceAndResetKeepsThem()
    {
        using var files = new TrustedAccessTemporaryDirectory();
        var cache = Path.Combine(files.Root, "frontend.cache.json");
        var legacy = Path.Combine(files.Root, "performance-overlay.local.json");
        var old = JsonSerializer.SerializeToNode(new FrontendPreferences
        {
            Overlays = new Dictionary<string, FrontendOverlayPresentation> { ["game"] = new() { Metrics = ["target.memory"] } }
        }, FrontendPreferencesOwner.Json)!.AsObject();
        old.Remove("overlaySelectionVersion");
        old["overlays"]!["game"]!.AsObject().Remove("enabled");
        old["overlays"]!["game"]!.AsObject().Remove("mode");
        File.WriteAllText(cache, old.ToJsonString());
        File.WriteAllText(legacy, """{"software":[{"softwareId":"game","enabled":true,"mode":"injected"}]}""");
        var owner = new FrontendPreferencesOwner(cache, null, legacy);
        var imported = owner.Read().Overlays["game"];
        Assert.True(imported.Enabled);
        Assert.Equal("injected", imported.Mode);
        Assert.Equal(["target.memory"], imported.Metrics);
        owner.Reset();
        var reset = new FrontendPreferencesOwner(cache, null, legacy).Read().Overlays["game"];
        Assert.True(reset.Enabled);
        Assert.Equal("injected", reset.Mode);
        Assert.Equal(FrontendPoints.MetricOrder, reset.Metrics);
        var disabled = owner.Read() with { Overlays = new Dictionary<string, FrontendOverlayPresentation> { ["game"] = reset with { Enabled = false } } };
        owner.Save(disabled);
        Assert.False(new FrontendPreferencesOwner(cache, null, legacy).Read().Overlays["game"].Enabled);
    }

    private static Task<byte[]> ReadCommitted(string path) =>
        ResourceManager.App.Infrastructure.NativeCore.WindowsNativeAtomicFileCommitter.ReadCommittedBytesAsync(path, default);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BackendAndFrontendStartupOrderPreservesLegacyChoicesAndBackup(int startupOrder)
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-overlay-migration-" + Guid.NewGuid().ToString("N"));
        var application = Directory.CreateDirectory(Path.Combine(root, "APP")).FullName;
        var config = Directory.CreateDirectory(Path.Combine(root, "Config")).FullName;
        var profiles = Directory.CreateDirectory(Path.Combine(root, "UserData", "SoftwareProfiles")).FullName;
        var appPath = Path.Combine(config, "app-settings.json");
        var overlayPath = Path.Combine(profiles, "performance-overlay.local.json");
        var cache = Path.Combine(config, "frontend.cache.json");
        try
        {
            var original = await File.ReadAllBytesAsync(Fixture("performance-overlay.local.json"));
            await File.WriteAllBytesAsync(appPath, await File.ReadAllBytesAsync(Fixture("app-settings.json")));
            await File.WriteAllBytesAsync(overlayPath, original);
            var frontend = new FrontendPreferencesOwner(cache, appPath, overlayPath);
            if (startupOrder == 1) Assert.Single(frontend.Read().Overlays);
            var environment = new Environment(application);
            async Task Migrate()
            {
                await new JsonAppSettingsStore(environment).LoadAsync(default);
            }
            if (startupOrder == 2)
                await Task.WhenAll(Task.Run(Migrate), Task.Run(() => frontend.Read()));
            else await Migrate();
            Assert.Equal(original, await ReadCommitted(overlayPath));
            var imported = frontend.Read();
            Assert.Equal(FrontendPoints.FontSizePx, imported.Overlays["legacy-game"].FontSizePx);
            frontend.Reset();
            Assert.Equal(imported.Overlays["legacy-game"].Mode, new FrontendPreferencesOwner(cache, appPath, overlayPath).Read().Overlays["legacy-game"].Mode);
        }
        finally
        {
            foreach (var directory in new[] { config, profiles })
            {
                foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
            Directory.Delete(Path.Combine(root, "UserData"));
            Directory.Delete(application);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void AppWireContainsNoPresentationOrFrontendRefreshPolicy()
    {
        var settings = AppSettingsDefaults.Create();
        var result = new AppSettingsUpdateResult(settings, DateTimeOffset.UtcNow, "settings",
            new(AppSettingsSourceKind.BundledFirstRun, settings.Version, "source", "effective", false));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonSerializerOptions.Web));
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("frontendRefreshIntervals", out _));
        Assert.False(root.GetProperty("settings").TryGetProperty("appearance", out _));
        var performance = root.GetProperty("settings").GetProperty("performance");
        foreach (var field in PresentationPreferenceRemoval.AppPerformanceFields)
            Assert.False(performance.TryGetProperty(field, out _), field);
    }

    [Fact]
    public async Task HeadWriterImageMigratesAtomicallyKeepsRawBackupAndIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-preference-migration-" + Guid.NewGuid().ToString("N"));
        var application = Directory.CreateDirectory(Path.Combine(root, "Resource Manager-APP")).FullName;
        var config = Directory.CreateDirectory(Path.Combine(root, "Config")).FullName;
        var path = Path.Combine(config, "app-settings.json");
        var original = await File.ReadAllBytesAsync(Fixture("app-settings.json"));
        try
        {
            await File.WriteAllBytesAsync(path, original);
            var owner = new JsonAppSettingsStore(new Environment(application));
            var loaded = await owner.LoadAsync(CancellationToken.None);
            Assert.Equal(AppSettingsDefaults.CurrentVersion, loaded.Settings.Version);
            Assert.True(loaded.Source.RewritePerformed);
            var backup = path + PresentationPreferenceRemoval.BackupSuffix;
            Assert.Equal(original, await ReadCommitted(backup));
            var backupTime = File.GetLastWriteTimeUtc(backup);
            using var migrated = JsonDocument.Parse(await ReadCommitted(path));
            Assert.False(PresentationPreferenceRemoval.HasAppFields(migrated.RootElement));
            var bytes = await ReadCommitted(path);
            var reloaded = await new JsonAppSettingsStore(new Environment(application)).LoadAsync(CancellationToken.None);
            Assert.False(reloaded.Source.RewritePerformed);
            Assert.Equal(bytes, await ReadCommitted(path));
            Assert.Equal(backupTime, File.GetLastWriteTimeUtc(backup));
            Assert.Equal(original, await ReadCommitted(backup));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(config)) File.Delete(file);
            Directory.Delete(config);
            Directory.Delete(application);
            Directory.Delete(root);
        }
    }

    internal static string Fixture(string name, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../Fixtures/FrontendPreferencesLegacy", name));

    private sealed class Environment(string path) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = path;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
