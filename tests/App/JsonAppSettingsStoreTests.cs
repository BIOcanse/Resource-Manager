using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class JsonAppSettingsStoreTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        "resource-manager-app-settings-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCommitFailurePreservesPreviousBackupAndReportsWhetherPrimaryCommitted(bool failBackup)
    {
        var appRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        var environment = new TestHostEnvironment(appRoot);
        var store = new JsonAppSettingsStore(environment);
        var original = await store.LoadAsync(CancellationToken.None);
        await store.SaveAsync(original.Settings, CancellationToken.None);
        var folder = Path.GetDirectoryName(original.StoragePath)!;
        var backup = Path.Combine(folder, "app-settings.last-good.json");
        var backupBytes = await File.ReadAllBytesAsync(backup);
        var readonlyPath = failBackup ? backup : original.StoragePath;
        File.SetAttributes(readonlyPath, FileAttributes.ReadOnly);
        try
        {
            var exception = await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(original.Settings with
            {
                SystemIntegration = original.Settings.SystemIntegration with { AutoStartEnabled = true }
            }, CancellationToken.None));
            Assert.Equal(failBackup, exception is AppSettingsCommitAmbiguousException);
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backup));
            Assert.Empty(Directory.EnumerateFiles(folder, "*.tmp"));
        }
        finally { File.SetAttributes(readonlyPath, FileAttributes.Normal); }
        var reloaded = await new JsonAppSettingsStore(environment).LoadAsync(CancellationToken.None);
        Assert.Equal(failBackup, reloaded.Settings.SystemIntegration.AutoStartEnabled);
    }

    [Theory]
    [InlineData("performance", "null")]
    [InlineData("performance", "[]")]
    [InlineData("appearance", "42")]
    [InlineData("systemIntegration", "false")]
    [InlineData("publicService", "null")]
    public async Task LoadAsync_WrongSectionKindUsesDefaultsAndKeepsOtherPreferences(string section, string literal)
    {
        var appRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        var config = Directory.CreateDirectory(Path.Combine(testRoot, "Config")).FullName;
        var source = JsonNode.Parse(JsonSerializer.Serialize(AppSettingsDefaults.Create(), JsonSerializerOptions.Web))!;
        source[section] = JsonNode.Parse(literal);
        source["debug"]!["debugLogEnabled"] = true;
        await File.WriteAllTextAsync(Path.Combine(config, "app-settings.json"), source.ToJsonString());
        var loaded = await new JsonAppSettingsStore(new TestHostEnvironment(appRoot)).LoadAsync(CancellationToken.None);
        Assert.True(loaded.Settings.Debug.DebugLogEnabled);
        Assert.False(loaded.Settings.SystemIntegration.AutoStartEnabled);
        var reloaded = await new JsonAppSettingsStore(new TestHostEnvironment(appRoot)).LoadAsync(CancellationToken.None);
        Assert.True(reloaded.Settings.Debug.DebugLogEnabled);
    }

    [Fact]
    public async Task SaveAsync_AutoStartSurvivesIndependentReloadAndCanBeDisabled()
    {
        var appRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        var environment = new TestHostEnvironment(appRoot);
        var store = new JsonAppSettingsStore(environment);
        var defaults = (await store.LoadAsync(CancellationToken.None)).Settings;
        await store.SaveAsync(defaults with
        {
            SystemIntegration = defaults.SystemIntegration with { AutoStartEnabled = true },
            Appearance = defaults.Appearance with { Theme = AppThemeModes.Dark }
        }, CancellationToken.None);
        var other = new JsonAppSettingsStore(environment);
        var loaded = (await other.LoadAsync(CancellationToken.None)).Settings;
        Assert.True(loaded.SystemIntegration.AutoStartEnabled);
        Assert.Equal(AppThemeModes.Dark, loaded.Appearance.Theme);
        await other.SaveAsync(loaded with { SystemIntegration = loaded.SystemIntegration with { AutoStartEnabled = false } }, CancellationToken.None);
        var disabled = (await new JsonAppSettingsStore(environment).LoadAsync(CancellationToken.None)).Settings;
        Assert.False(disabled.SystemIntegration.AutoStartEnabled);
        Assert.Equal(AppThemeModes.Dark, disabled.Appearance.Theme);
    }

    [Fact]
    public async Task SaveAsync_NativeUiReaderDoesNotBlockAtomicReplacement()
    {
        var appRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        var store = new JsonAppSettingsStore(new TestHostEnvironment(appRoot));
        var original = await store.LoadAsync(CancellationToken.None);
        await store.SaveAsync(original.Settings, CancellationToken.None);
        using var reader = ResourceManager.NativeUi.Configuration.SettingsFileReader.Open(original.StoragePath);
        await store.SaveAsync(original.Settings with
        {
            SystemIntegration = original.Settings.SystemIntegration with { AutoStartEnabled = true }
        }, CancellationToken.None);
        using var oldImage = await JsonDocument.ParseAsync(reader);
        using var newImage = JsonDocument.Parse(await File.ReadAllTextAsync(original.StoragePath));
        Assert.False(oldImage.RootElement.GetProperty("systemIntegration").GetProperty("autoStartEnabled").GetBoolean());
        Assert.True(newImage.RootElement.GetProperty("systemIntegration").GetProperty("autoStartEnabled").GetBoolean());
    }

    [Fact]
    public async Task LoadAsync_RewritesVersion117CoordinatorKeysToCurrentVersionOnce()
    {
        var appRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        var configRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Config")).FullName;
        var settingsPath = Path.Combine(configRoot, "app-settings.json");
        var source = JsonNode.Parse(JsonSerializer.Serialize(
            AppSettingsDefaults.Create(),
            JsonSerializerOptions.Web))!.AsObject();
        source["version"] = "1.0.17";
        var debug = source["debug"]!.AsObject();
        debug.Remove("hostManagerSmartCoordinatorScoreOnlyEnabled");
        debug.Remove("hostManagerSmartCoordinatorPerformanceLogEnabled");
        debug["smartOptimizationScoreOnlyEnabled"] = true;
        debug["smartOptimizationPerformanceLogEnabled"] = true;
        source["selfOptimization"] = new JsonObject
        {
            ["enabled"] = true,
            ["loopIntervalSeconds"] = 30
        };
        await File.WriteAllTextAsync(settingsPath, source.ToJsonString());

        var store = new JsonAppSettingsStore(new TestHostEnvironment(appRoot));
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(AppSettingsSourceKind.MigratedPersisted, loaded.Source.Kind);
        Assert.Equal("1.0.17", loaded.Source.SourceVersion);
        Assert.True(loaded.Source.RewritePerformed);
        Assert.True(loaded.Settings.Debug.HostManagerSmartCoordinatorScoreOnlyEnabled);
        Assert.True(loaded.Settings.Debug.HostManagerSmartCoordinatorPerformanceLogEnabled);

        var persisted = await File.ReadAllTextAsync(settingsPath);
        Assert.Contains($"\"version\": \"{AppSettingsDefaults.CurrentVersion}\"", persisted, StringComparison.Ordinal);
        Assert.Contains("hostManagerSmartCoordinatorScoreOnlyEnabled", persisted, StringComparison.Ordinal);
        Assert.Contains("hostManagerSmartCoordinatorPerformanceLogEnabled", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("smartOptimizationScoreOnlyEnabled", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("smartOptimizationPerformanceLogEnabled", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("selfOptimization", persisted, StringComparison.Ordinal);

        var reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Same(loaded.Settings, reloaded.Settings);
        Assert.Equal(loaded.Source, reloaded.Source);
    }

    [Fact]
    public async Task LoadAsync_MigratesVersion122WithoutResettingUserSettings()
    {
        var caseRoot = Path.Combine(testRoot, "version-122-migration");
        var appRoot = Directory.CreateDirectory(
            Path.Combine(caseRoot, "Resource Manager-APP")).FullName;
        var configRoot = Directory.CreateDirectory(Path.Combine(caseRoot, "Config")).FullName;
        var settingsPath = Path.Combine(configRoot, "app-settings.json");
        var source = JsonNode.Parse(JsonSerializer.Serialize(
            AppSettingsDefaults.Create(),
            JsonSerializerOptions.Web))!.AsObject();
        source["version"] = "1.0.22";
        var performance = source["performance"]!.AsObject();
        performance["samplingDispatchMode"] = "normal";
        performance["optimizationMode"] = AppOptimizationModes.Smart;
        performance["monitorRefreshIntervalMs"]!["mode"] = AppPresetNumericSettingModes.Custom;
        performance["monitorRefreshIntervalMs"]!["customValue"] = 4_321;
        source["appearance"]!["theme"] = AppThemeModes.Dark;
        source["appearance"]!["language"] = AppLanguageModes.English;
        source["debug"]!["hostManagerSmartCoordinatorScoreOnlyEnabled"] = true;
        await File.WriteAllTextAsync(settingsPath, source.ToJsonString());

        var loaded = await new JsonAppSettingsStore(new TestHostEnvironment(appRoot))
            .LoadAsync(CancellationToken.None);

        Assert.Equal(AppSettingsSourceKind.MigratedPersisted, loaded.Source.Kind);
        Assert.Equal("1.0.22", loaded.Source.SourceVersion);
        Assert.True(loaded.Source.RewritePerformed);
        Assert.Equal(AppSettingsDefaults.CurrentVersion, loaded.Settings.Version);
        Assert.Equal(AppOptimizationModes.Smart, loaded.Settings.Performance.OptimizationMode);
        Assert.Equal(
            AppPresetNumericSettingModes.Custom,
            loaded.Settings.Performance.MonitorRefreshIntervalMs.Mode);
        Assert.Equal(4_321, loaded.Settings.Performance.MonitorRefreshIntervalMs.CustomValue);
        Assert.Equal(AppThemeModes.Dark, loaded.Settings.Appearance.Theme);
        Assert.Equal(AppLanguageModes.English, loaded.Settings.Appearance.Language);
        Assert.True(loaded.Settings.Debug.HostManagerSmartCoordinatorScoreOnlyEnabled);

        using var persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(settingsPath));
        Assert.Equal(
            AppSettingsDefaults.CurrentVersion,
            persisted.RootElement.GetProperty("version").GetString());
        Assert.False(
            persisted.RootElement
                .GetProperty("performance")
                .TryGetProperty("samplingDispatchMode", out _));
        Assert.Equal(
            AppThemeModes.Dark,
            persisted.RootElement
                .GetProperty("appearance")
                .GetProperty("theme")
                .GetString());
    }

    [Fact]
    public async Task LoadReadOnlyAsync_MigratesInMemoryWithoutChangingTheFileTree()
    {
        var caseRoot = Path.Combine(testRoot, "read-only-migration");
        var appRoot = Directory.CreateDirectory(
            Path.Combine(caseRoot, "Resource Manager-APP")).FullName;
        var configRoot = Directory.CreateDirectory(Path.Combine(caseRoot, "Config")).FullName;
        var settingsPath = Path.Combine(configRoot, "app-settings.json");
        var source = JsonNode.Parse(JsonSerializer.Serialize(
            AppSettingsDefaults.Create(),
            JsonSerializerOptions.Web))!.AsObject();
        source["version"] = "1.0.17";
        await File.WriteAllTextAsync(settingsPath, source.ToJsonString());
        var before = CaptureFileTree(configRoot);

        var loaded = await new JsonAppSettingsStore(new TestHostEnvironment(appRoot))
            .LoadReadOnlyAsync(CancellationToken.None);

        Assert.Equal(AppSettingsSourceKind.MigratedPersisted, loaded.Source.Kind);
        Assert.False(loaded.Source.RewritePerformed);
        Assert.Equal(AppSettingsDefaults.CurrentVersion, loaded.Settings.Version);
        AssertFileTreeUnchanged(before, configRoot);
        Assert.False(File.Exists(Path.Combine(configRoot, "app-settings.last-good.json")));
    }

    [Fact]
    public async Task LoadReadOnlyAsync_UsesLastKnownGoodWithoutQuarantineOrRestore()
    {
        var caseRoot = Path.Combine(testRoot, "read-only-last-good");
        var appRoot = Directory.CreateDirectory(
            Path.Combine(caseRoot, "Resource Manager-APP")).FullName;
        var store = new JsonAppSettingsStore(new TestHostEnvironment(appRoot));
        var expected = AppSettingsDefaults.Create() with
        {
            Appearance = AppSettingsDefaults.Create().Appearance with
            {
                Theme = AppThemeModes.Dark
            }
        };
        var saved = await store.SaveAsync(expected, CancellationToken.None);
        await File.WriteAllTextAsync(saved.StoragePath, "{");
        var configRoot = Path.GetDirectoryName(saved.StoragePath)!;
        var before = CaptureFileTree(configRoot);

        var recovered = await new JsonAppSettingsStore(new TestHostEnvironment(appRoot))
            .LoadReadOnlyAsync(CancellationToken.None);

        Assert.Equal(AppSettingsSourceKind.RecoveredLastKnownGood, recovered.Source.Kind);
        Assert.Equal("readOnlyLastKnownGoodFallback", recovered.Source.RecoveryDisposition);
        Assert.Null(recovered.Source.RecoveryArtifactPath);
        Assert.Equal(AppThemeModes.Dark, recovered.Settings.Appearance.Theme);
        AssertFileTreeUnchanged(before, configRoot);
    }

    [Fact]
    public async Task SaveValidatedAsync_ValidationFailureLeavesDurableSettingsUnchanged()
    {
        var appRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "validated-failure", "Resource Manager-APP")).FullName;
        var store = new JsonAppSettingsStore(new TestHostEnvironment(appRoot));
        var original = AppSettingsDefaults.Create();
        var saved = await store.SaveAsync(original, CancellationToken.None);
        var originalPayload = await File.ReadAllBytesAsync(saved.StoragePath);
        var candidate = original with
        {
            Performance = original.Performance with
            {
                OptimizationMode = AppOptimizationModes.Smart
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveValidatedAsync(
                candidate,
                static (_, _) => throw new InvalidOperationException("compile rejected"),
                CancellationToken.None));

        Assert.Equal(originalPayload, await File.ReadAllBytesAsync(saved.StoragePath));
        Assert.Equal(
            JsonSerializer.Serialize(original),
            JsonSerializer.Serialize((await store.LoadAsync(CancellationToken.None)).Settings));
    }

    [Fact]
    public async Task SaveValidatedAsync_CommitsTheExactValidatedImage()
    {
        var appRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "validated-success", "Resource Manager-APP")).FullName;
        var store = new JsonAppSettingsStore(new TestHostEnvironment(appRoot));
        var candidate = AppSettingsDefaults.Create();
        AppSettingsUpdateResult? validated = null;

        var committed = await store.SaveValidatedAsync(
            candidate,
            (staged, _) =>
            {
                validated = staged;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.NotNull(validated);
        Assert.Equal(validated, committed);
        Assert.Equal(
            JsonSerializer.Serialize(candidate),
            JsonSerializer.Serialize((await store.LoadAsync(CancellationToken.None)).Settings));
    }

    [Fact]
    public async Task LoadAsync_QuarantinesCorruptPrimaryAndRestoresLastKnownGood()
    {
        var appRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "last-known-good", "Resource Manager-APP")).FullName;
        var originalStore = new JsonAppSettingsStore(new TestHostEnvironment(appRoot));
        var expected = AppSettingsDefaults.Create() with
        {
            Appearance = AppSettingsDefaults.Create().Appearance with
            {
                Theme = AppThemeModes.Dark
            }
        };
        var saved = await originalStore.SaveAsync(expected, CancellationToken.None);
        var lastKnownGoodPath = Path.Combine(
            Path.GetDirectoryName(saved.StoragePath)!,
            "app-settings.last-good.json");
        Assert.True(File.Exists(lastKnownGoodPath));
        await File.WriteAllTextAsync(saved.StoragePath, "{");

        var recoveredStore = new JsonAppSettingsStore(new TestHostEnvironment(appRoot));
        var recovered = await recoveredStore.LoadAsync(CancellationToken.None);

        Assert.Equal(AppSettingsSourceKind.RecoveredLastKnownGood, recovered.Source.Kind);
        Assert.Equal("recoveredLastKnownGood", recovered.Source.RecoveryDisposition);
        Assert.NotNull(recovered.Source.RecoveryArtifactPath);
        Assert.True(File.Exists(recovered.Source.RecoveryArtifactPath));
        Assert.Equal(AppThemeModes.Dark, recovered.Settings.Appearance.Theme);
        Assert.Equal(
            JsonSerializer.Serialize(expected),
            JsonSerializer.Serialize(
                (await new JsonAppSettingsStore(new TestHostEnvironment(appRoot))
                    .LoadAsync(CancellationToken.None)).Settings));
    }

    [Fact]
    public async Task LoadAsync_QuarantinesCorruptPrimaryAndPersistsExplicitSafeDefaults()
    {
        var appRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "corrupt-without-backup", "Resource Manager-APP")).FullName;
        var configRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "corrupt-without-backup", "Config")).FullName;
        var settingsPath = Path.Combine(configRoot, "app-settings.json");
        await File.WriteAllTextAsync(settingsPath, "{");

        var store = new JsonAppSettingsStore(new TestHostEnvironment(appRoot));
        var recovered = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(
            AppSettingsSourceKind.RecoveredDefaultsAfterCorruption,
            recovered.Source.Kind);
        Assert.Equal(
            "recoveredDefaultsAfterCorruption",
            recovered.Source.RecoveryDisposition);
        Assert.True(File.Exists(settingsPath));
        Assert.True(File.Exists(Path.Combine(configRoot, "app-settings.last-good.json")));
        Assert.NotNull(recovered.Source.RecoveryArtifactPath);
        Assert.True(File.Exists(recovered.Source.RecoveryArtifactPath));
        using var persisted = JsonDocument.Parse(
            await File.ReadAllBytesAsync(settingsPath));
        Assert.Equal(
            AppSettingsDefaults.CurrentVersion,
            persisted.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task LoadAsync_RejectsUnsupportedSchemaWithoutQuarantiningOrRewriting()
    {
        var appRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "unsupported-schema", "Resource Manager-APP")).FullName;
        var configRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "unsupported-schema", "Config")).FullName;
        var settingsPath = Path.Combine(configRoot, "app-settings.json");
        const string futureSettings = "{\"version\":\"99.0.0\"}";
        await File.WriteAllTextAsync(settingsPath, futureSettings);
        var store = new JsonAppSettingsStore(new TestHostEnvironment(appRoot));

        await Assert.ThrowsAnyAsync<IOException>(() =>
            store.LoadAsync(CancellationToken.None));

        Assert.Equal(futureSettings, await File.ReadAllTextAsync(settingsPath));
        Assert.Empty(Directory.EnumerateFiles(configRoot, "*.corrupt.*"));
    }

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

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
