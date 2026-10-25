using System.Text.Json;
using ResourceManager.NativeUi.Preferences;

namespace Resource_Manager_APP.Tests;

public sealed class FrontendPreferencesCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rm-preference-cache-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(root, "frontend-preferences.cache.json");
    private string App => Path.Combine(root, "app-settings.json");
    private string Overlay => Path.Combine(root, "performance-overlay.local.json");

    public FrontendPreferencesCacheTests() => Directory.CreateDirectory(root);

    [Fact]
    public void MissingCacheReadsGeneratedPointsWithoutCreatingAUserChoice()
    {
        var current = Owner().Read();
        Assert.Equal(FrontendPoints.UiStyle, current.Appearance.UiStyle);
        Assert.Equal(FrontendPoints.Theme, current.Appearance.Theme);
        Assert.Equal(FrontendPoints.BarFrameRate, current.Appearance.ResourceBarFrameRate);
        Assert.Equal(FrontendPoints.Monitor.CustomValue, current.Refresh.Monitor.CustomValue);
        Assert.Equal(FrontendPoints.LocalSystem.Preset, current.Refresh.LocalSystem.Preset);
        Assert.Equal(FrontendPoints.MetricOrder, Owner().Overlay("unrecorded").Metrics);
        Assert.False(File.Exists(Cache));
    }

    [Fact]
    public void UserChoicesSurviveOwnerRestartAndResetRestoresPoints()
    {
        var owner = Owner();
        var desired = owner.Read() with
        {
            Appearance = new() { Theme = "dark", ResourceBarFrameRate = 120 },
            Refresh = new() { Monitor = new("custom", "balanced", 3456) },
            Overlays = new Dictionary<string, FrontendOverlayPresentation>
            { ["game"] = new() { FontSizePx = 22, Metrics = ["custom.metric", "target.cpu"] } }
        };
        owner.Save(desired);
        var restarted = Owner();
        var current = restarted.Read();
        Assert.Equal("dark", current.Appearance.Theme);
        Assert.Equal(3456, current.Refresh.Monitor.CustomValue);
        Assert.Equal(22, restarted.Overlay("GAME").FontSizePx);
        Assert.Equal(new[] { "custom.metric", "target.cpu" }, restarted.Overlay("game").Metrics);
        restarted.Reset();
        Assert.Equal(Serialize(new FrontendPreferences()), Serialize(Owner().Read()));
    }

    [Fact]
    public void LegacyBackupsImportOnceAndLosingCacheNeverReimports()
    {
        WriteLegacy();
        // Backend may already have migrated its live file before the frontend starts.
        File.Move(App, App + FrontendPreferencesOwner.LegacyBackupSuffix);
        File.WriteAllText(App, "{\"version\":\"1.0.32\",\"performance\":{}}");
        File.Move(Overlay, Overlay + FrontendPreferencesOwner.LegacyBackupSuffix);
        File.WriteAllText(Overlay, "{\"version\":2,\"software\":[{\"softwareId\":\"game\",\"enabled\":true,\"mode\":\"injected\"}]}");
        var imported = Owner().Read();
        Assert.Equal("dark", imported.Appearance.Theme);
        Assert.Equal("all", imported.Appearance.ReadingSourceMode);
        Assert.Equal("disabled", imported.Refresh.SmartMonitoringMode);
        Assert.Equal(4321, imported.Refresh.Monitor.CustomValue);
        Assert.Equal(24, imported.Overlays["game"].FontSizePx);
        Assert.Equal(new[] { "target.gpu", "target.fps" }, imported.Overlays["game"].Metrics);
        Assert.True(File.Exists(Cache + ".imported-v1"));
        File.Delete(Cache);
        Assert.Equal(Serialize(new FrontendPreferences()), Serialize(Owner().Read()));
        Assert.False(File.Exists(Cache));
    }

    [Fact]
    public void FrontendCanImportBeforeBackendReplacesTheOldFile()
    {
        WriteLegacy();
        var imported = Owner().Read();
        Assert.Equal("dark", imported.Appearance.Theme);
        Assert.Equal(24, imported.Overlays["game"].FontSizePx);
        Assert.True(File.ReadAllText(App).Contains("appearance", StringComparison.Ordinal));
        Assert.True(File.ReadAllText(Overlay).Contains("fontSizePx", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingCacheWinsEvenWhenImportMarkerWasNotYetCommitted()
    {
        WriteLegacy();
        var desired = new FrontendPreferences { Appearance = new() { Theme = "lowContrast" } };
        File.WriteAllText(Cache, Serialize(desired));
        Assert.Equal("lowContrast", Owner().Read().Appearance.Theme);
        Assert.True(File.Exists(Cache + ".imported-v1"));
    }

    [Fact]
    public void MalformedLegacyFileDoesNotStopFrontendStartup()
    {
        File.WriteAllText(App, "{invalid");
        Assert.Equal(FrontendPoints.Theme, Owner().Read().Appearance.Theme);
        Assert.Equal("{invalid", File.ReadAllText(App));
    }

    [Fact]
    public void ScopedUpdatesPreserveChoicesMadeFromAnotherFrontendView()
    {
        var owner = Owner();
        var old = owner.Read();
        owner.SaveOverlay("game", old with { Overlays = new Dictionary<string, FrontendOverlayPresentation>
            { ["GAME"] = new() { FontSizePx = 28 } } });
        owner.SaveGlobal(old with { Appearance = new() { Theme = "dark" } });
        Assert.Equal(28, owner.Overlay("game").FontSizePx);
        owner.SaveOverlay("other", old with { Overlays = new Dictionary<string, FrontendOverlayPresentation>
            { ["other"] = new() { FontSizePx = 30 } } });
        Assert.Equal("dark", owner.Read().Appearance.Theme);
        Assert.Equal(28, owner.Overlay("game").FontSizePx);
        Assert.Equal(30, owner.Overlay("other").FontSizePx);
    }

    [Fact]
    public void ActualHeadWriterFixturesImportWithoutInventingNewDefaults()
    {
        File.Copy(BackendPreferenceBoundaryTests.Fixture("app-settings.json"), App);
        File.Copy(BackendPreferenceBoundaryTests.Fixture("performance-overlay.local.json"), Overlay);
        var imported = Owner().Read();
        Assert.Equal(FrontendPoints.Theme, imported.Appearance.Theme);
        Assert.Equal(FrontendPoints.MetricOrder, imported.Overlays["legacy-game"].Metrics);
        Assert.Equal(FrontendPoints.FontSizePx, imported.Overlays["legacy-game"].FontSizePx);
        Assert.True(File.Exists(Cache));
    }

    [Fact]
    public void InvalidCacheIsReportedInsteadOfSilentlyReplacingAUserChoice()
    {
        File.WriteAllText(Cache, "{invalid");
        Assert.Throws<JsonException>(() => Owner().Read());
        Assert.Equal("{invalid", File.ReadAllText(Cache));
    }

    [Fact]
    public void InvalidLegacyValuesCannotPreventFrontendStartup()
    {
        File.WriteAllText(App, """{"appearance":{"theme":"invalid"}}""");
        Assert.Equal(FrontendPoints.Theme, Owner().Read().Appearance.Theme);
        Assert.False(File.Exists(Cache));
        Assert.True(File.Exists(Cache + ".imported-v1"));
    }

    [Fact]
    public void InvalidUpdateCannotReplaceTheCommittedCache()
    {
        var owner = Owner();
        owner.Save(new() { Appearance = new() { Theme = "dark" } });
        Assert.Throws<InvalidDataException>(() => owner.Save(new()
        {
            Overlays = new Dictionary<string, FrontendOverlayPresentation>
            { ["game"] = new() { RegionWidthRatio = double.NaN } }
        }));
        Assert.Equal("dark", Owner().Read().Appearance.Theme);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    private FrontendPreferencesOwner Owner() => new(Cache, App, Overlay);
    private static string Serialize(FrontendPreferences value) => JsonSerializer.Serialize(value, FrontendPreferencesOwner.Json);

    private void WriteLegacy()
    {
        // Exact property names and object layout of HEAD's app/overlay writers.
        File.WriteAllText(App, """
            {"version":"1.0.31","appearance":{"uiStyle":"standard","theme":"dark",
            "animations":"normal","resourceBarFrameRateMode":"custom","resourceBarFrameRate":90,
            "barColorMode":"distinct","fontSmoothing":"grayscale","language":"de-DE",
            "byteUnitMode":"binary","readingSourceMode":"all"},"performance":{
            "smartMonitoringMode":"disabled","monitorRefreshIntervalMs":{"mode":"custom","preset":"balanced","customValue":4321},
            "resourceTableRefreshIntervalMs":{"mode":"preset","preset":"quiet","customValue":1000},
            "localSystemRefreshIntervalMs":{"mode":"aotu","preset":"balanced","customValue":60000}}}
            """);
        File.WriteAllText(Overlay, """
            {"version":1,"software":[{"softwareId":"game","enabled":true,"mode":"injected",
            "metrics":["target.gpu","target.fps"],"sizeMode":"windowRatio","fontSizePx":24,
            "regionWidthRatio":0.4,"regionHeightRatio":0.3,"anchor":"bottomRight","marginPx":30}]}
            """);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
        Directory.Delete(root);
    }
}
