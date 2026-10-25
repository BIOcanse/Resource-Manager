using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Settings;
using ResourceManager.App.Infrastructure.Overlay;
using ResourceManager.NativeUi.Preferences;

namespace Resource_Manager_APP.Tests;

public sealed class BackendPreferenceBoundaryTests
{
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
            var backend = new JsonPerformanceOverlaySettingsStore(environment);
            async Task Migrate()
            {
                await new JsonAppSettingsStore(environment).LoadAsync(default);
                await backend.GetAsync(default);
            }
            if (startupOrder == 2)
                await Task.WhenAll(Task.Run(Migrate), Task.Run(() => frontend.Read()));
            else await Migrate();
            Assert.Equal(2, (await backend.GetAsync(default)).Version);
            using var migrated = JsonDocument.Parse(await File.ReadAllBytesAsync(overlayPath));
            Assert.False(PresentationPreferenceRemoval.HasOverlayLayoutFields(migrated.RootElement));
            var backup = overlayPath + PresentationPreferenceRemoval.BackupSuffix;
            Assert.Equal(original, await File.ReadAllBytesAsync(backup));
            var bytes = await File.ReadAllBytesAsync(overlayPath);
            var backupTime = File.GetLastWriteTimeUtc(backup);
            await new JsonPerformanceOverlaySettingsStore(environment).GetAsync(default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(overlayPath));
            Assert.Equal(backupTime, File.GetLastWriteTimeUtc(backup));
            var imported = frontend.Read();
            Assert.Equal(FrontendPoints.FontSizePx, imported.Overlays["legacy-game"].FontSizePx);
            frontend.Reset();
            Assert.Empty(new FrontendPreferencesOwner(cache, appPath, overlayPath).Read().Overlays);
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
            Assert.Equal(original, await File.ReadAllBytesAsync(backup));
            var backupTime = File.GetLastWriteTimeUtc(backup);
            using var migrated = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
            Assert.False(PresentationPreferenceRemoval.HasAppFields(migrated.RootElement));
            var bytes = await File.ReadAllBytesAsync(path);
            var reloaded = await new JsonAppSettingsStore(new Environment(application)).LoadAsync(CancellationToken.None);
            Assert.False(reloaded.Source.RewritePerformed);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(backupTime, File.GetLastWriteTimeUtc(backup));
            Assert.Equal(original, await File.ReadAllBytesAsync(backup));
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
