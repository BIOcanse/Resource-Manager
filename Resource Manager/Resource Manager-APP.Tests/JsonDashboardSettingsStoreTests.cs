using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class JsonDashboardSettingsStoreTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        "resource-manager-dashboard-settings-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadAsync_NullColumnsAndBindingKindDoNotLoseValidPreferences()
    {
        var appRoot = CreateAppRoot("null-members");
        var config = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(appRoot)!, "Config")).FullName;
        var source = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(DashboardSettingsDefaults.Create(), JsonSerializerOptions.Web))!;
        source["resourceTableColumns"] = System.Text.Json.Nodes.JsonNode.Parse("""
            [null, {"id":"gpu.1.vram","visible":false,"width":179,"binding":{"scopeKind":null,"scopeKey":"gpu-dedicated"}}]
            """);
        await File.WriteAllTextAsync(Path.Combine(config, "dashboard-settings.json"), source.ToJsonString());
        var loaded = await new JsonDashboardSettingsStore(new TestHostEnvironment(appRoot)).LoadAsync(CancellationToken.None);
        var column = Assert.Single(loaded.Settings.ResourceTableColumns, item => item.Id == "gpu.1.vram");
        Assert.False(column.Visible);
        Assert.Equal(179, column.Width);
        Assert.Null(column.Binding);
    }

    [Fact]
    public async Task SaveAsync_CommitsCanonicalAndLastKnownGoodImagesWithoutTempResidue()
    {
        var appRoot = CreateAppRoot("save");
        var store = new JsonDashboardSettingsStore(new TestHostEnvironment(appRoot));
        var candidate = DashboardSettingsDefaults.Create() with
        {
            ResourceTableColumns =
            [
                new ResourceTableColumnSettings("name", true, 312)
            ]
        };

        var saved = await store.SaveAsync(candidate, CancellationToken.None);
        var configRoot = Path.GetDirectoryName(saved.StoragePath)!;
        var lastKnownGoodPath = Path.Combine(
            configRoot,
            "dashboard-settings.last-good.json");

        Assert.Equal(DashboardSettingsSourceKind.SavedPersisted, saved.Source.Kind);
        Assert.True(File.Exists(saved.StoragePath));
        Assert.True(File.Exists(lastKnownGoodPath));
        Assert.Equal(
            await File.ReadAllBytesAsync(saved.StoragePath),
            await File.ReadAllBytesAsync(lastKnownGoodPath));
        Assert.Empty(Directory.EnumerateFiles(configRoot, "*.tmp"));

        var reloaded = await new JsonDashboardSettingsStore(
                new TestHostEnvironment(appRoot))
            .LoadAsync(CancellationToken.None);
        Assert.Equal(DashboardSettingsSourceKind.Persisted, reloaded.Source.Kind);
        Assert.Equal(
            JsonSerializer.Serialize(saved.Settings),
            JsonSerializer.Serialize(reloaded.Settings));
    }

    [Fact]
    public async Task LoadReadOnlyAsync_DoesNotCreateLastKnownGoodOrChangeCanonical()
    {
        var caseRoot = Path.Combine(testRoot, "read-only-persisted");
        var appRoot = Directory.CreateDirectory(
            Path.Combine(caseRoot, "Resource Manager-APP")).FullName;
        var configRoot = Directory.CreateDirectory(Path.Combine(caseRoot, "Config")).FullName;
        var settingsPath = Path.Combine(configRoot, "dashboard-settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(
                DashboardSettingsDefaults.Create(),
                JsonSerializerOptions.Web));
        var before = CaptureFileTree(configRoot);

        var loaded = await new JsonDashboardSettingsStore(new TestHostEnvironment(appRoot))
            .LoadReadOnlyAsync(CancellationToken.None);

        Assert.Equal(DashboardSettingsSourceKind.Persisted, loaded.Source.Kind);
        Assert.False(loaded.Source.RewritePerformed);
        AssertFileTreeUnchanged(before, configRoot);
        Assert.False(File.Exists(Path.Combine(configRoot, "dashboard-settings.last-good.json")));
    }

    [Fact]
    public async Task LoadReadOnlyAsync_UsesLastKnownGoodWithoutQuarantineOrRestore()
    {
        var appRoot = CreateAppRoot("read-only-last-good");
        var store = new JsonDashboardSettingsStore(new TestHostEnvironment(appRoot));
        var expected = DashboardSettingsDefaults.Create() with
        {
            ResourceBars =
            [
                new ResourceBarSettings("resource-cpu", "cpu.usage", "active")
            ]
        };
        var saved = await store.SaveAsync(expected, CancellationToken.None);
        await File.WriteAllTextAsync(saved.StoragePath, "{");
        var configRoot = Path.GetDirectoryName(saved.StoragePath)!;
        var before = CaptureFileTree(configRoot);

        var recovered = await new JsonDashboardSettingsStore(new TestHostEnvironment(appRoot))
            .LoadReadOnlyAsync(CancellationToken.None);

        Assert.Equal(DashboardSettingsSourceKind.RecoveredLastKnownGood, recovered.Source.Kind);
        Assert.Equal("readOnlyLastKnownGoodFallback", recovered.Source.RecoveryDisposition);
        Assert.Null(recovered.Source.RecoveryArtifactPath);
        Assert.Equal(
            JsonSerializer.Serialize(saved.Settings),
            JsonSerializer.Serialize(recovered.Settings));
        AssertFileTreeUnchanged(before, configRoot);
    }

    [Fact]
    public async Task LoadAsync_QuarantinesCorruptPrimaryAndRestoresLastKnownGood()
    {
        var appRoot = CreateAppRoot("recover-last-good");
        var originalStore = new JsonDashboardSettingsStore(
            new TestHostEnvironment(appRoot));
        var expected = DashboardSettingsDefaults.Create() with
        {
            ResourceBars =
            [
                new ResourceBarSettings("resource-cpu", "cpu.usage", "active")
            ]
        };
        var saved = await originalStore.SaveAsync(expected, CancellationToken.None);
        await File.WriteAllTextAsync(saved.StoragePath, "{");

        var recovered = await new JsonDashboardSettingsStore(
                new TestHostEnvironment(appRoot))
            .LoadAsync(CancellationToken.None);

        Assert.Equal(
            DashboardSettingsSourceKind.RecoveredLastKnownGood,
            recovered.Source.Kind);
        Assert.Equal(
            "recoveredLastKnownGood",
            recovered.Source.RecoveryDisposition);
        Assert.NotNull(recovered.Source.RecoveryArtifactPath);
        Assert.True(File.Exists(recovered.Source.RecoveryArtifactPath));
        Assert.Equal(
            JsonSerializer.Serialize(saved.Settings),
            JsonSerializer.Serialize(recovered.Settings));
        Assert.Equal(
            JsonSerializer.Serialize(saved.Settings),
            JsonSerializer.Serialize(
                (await new JsonDashboardSettingsStore(
                        new TestHostEnvironment(appRoot))
                    .LoadAsync(CancellationToken.None)).Settings));
    }

    [Fact]
    public async Task LoadAsync_QuarantinesCorruptPrimaryAndPersistsExplicitDefaults()
    {
        var appRoot = CreateAppRoot("recover-defaults");
        var configRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "recover-defaults", "Config")).FullName;
        var settingsPath = Path.Combine(configRoot, "dashboard-settings.json");
        await File.WriteAllTextAsync(settingsPath, "{");

        var recovered = await new JsonDashboardSettingsStore(
                new TestHostEnvironment(appRoot))
            .LoadAsync(CancellationToken.None);

        Assert.Equal(
            DashboardSettingsSourceKind.RecoveredDefaultsAfterCorruption,
            recovered.Source.Kind);
        Assert.Equal(
            "recoveredDefaultsAfterCorruption",
            recovered.Source.RecoveryDisposition);
        Assert.True(File.Exists(settingsPath));
        Assert.True(File.Exists(Path.Combine(
            configRoot,
            "dashboard-settings.last-good.json")));
        Assert.NotNull(recovered.Source.RecoveryArtifactPath);
        Assert.True(File.Exists(recovered.Source.RecoveryArtifactPath));
        using var persisted = JsonDocument.Parse(
            await File.ReadAllBytesAsync(settingsPath));
        Assert.Equal(
            DashboardSettingsDefaults.CurrentVersion,
            persisted.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task LoadAsync_RejectsUnsupportedSchemaWithoutQuarantineOrRewrite()
    {
        var appRoot = CreateAppRoot("unsupported-schema");
        var configRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "unsupported-schema", "Config")).FullName;
        var settingsPath = Path.Combine(configRoot, "dashboard-settings.json");
        const string futureSettings = "{\"version\":99}";
        await File.WriteAllTextAsync(settingsPath, futureSettings);

        var store = new JsonDashboardSettingsStore(new TestHostEnvironment(appRoot));
        await Assert.ThrowsAnyAsync<IOException>(() =>
            store.LoadAsync(CancellationToken.None));

        Assert.Equal(futureSettings, await File.ReadAllTextAsync(settingsPath));
        Assert.Empty(Directory.EnumerateFiles(configRoot, "*.corrupt.*"));
        Assert.False(File.Exists(Path.Combine(
            configRoot,
            "dashboard-settings.last-good.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private string CreateAppRoot(string caseName)
        => Directory.CreateDirectory(
            Path.Combine(testRoot, caseName, "Resource Manager-APP")).FullName;

    private static Dictionary<string, FileSnapshot> CaptureFileTree(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => new FileSnapshot(
                    File.ReadAllBytes(path),
                    File.GetLastWriteTimeUtc(path)),
                StringComparer.OrdinalIgnoreCase);

    private static void AssertFileTreeUnchanged(
        IReadOnlyDictionary<string, FileSnapshot> before,
        string root)
    {
        var after = CaptureFileTree(root);
        Assert.Equal(before.Keys.OrderBy(static path => path), after.Keys.OrderBy(static path => path));
        foreach (var (path, expected) in before)
        {
            var actual = after[path];
            Assert.Equal(expected.Bytes, actual.Bytes);
            Assert.Equal(expected.LastWriteTimeUtc, actual.LastWriteTimeUtc);
        }
    }

    private sealed record FileSnapshot(byte[] Bytes, DateTime LastWriteTimeUtc);

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
