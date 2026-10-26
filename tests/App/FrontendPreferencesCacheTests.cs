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
            { ["game"] = new() { Enabled = true, Mode = "injected", FontSizePx = 22, Metrics = ["custom.metric", "target.cpu"] } }
        };
        owner.Save(desired);
        var restarted = Owner();
        var current = restarted.Read();
        Assert.Equal("dark", current.Appearance.Theme);
        Assert.Equal(3456, current.Refresh.Monitor.CustomValue);
        Assert.Equal(22, restarted.Overlay("GAME").FontSizePx);
        Assert.Equal(new[] { "custom.metric", "target.cpu" }, restarted.Overlay("game").Metrics);
        restarted.Reset();
        var expected = new FrontendPreferences { Overlays = new Dictionary<string, FrontendOverlayPresentation>
            { ["game"] = new() { Enabled = true, Mode = "injected" } } };
        Assert.Equal(Serialize(expected), Serialize(Owner().Read()));
    }

    [Fact]
    public void SparseCacheUsesPointDefaultsAndPreservesTheSelectedLanguage()
    {
        File.WriteAllText(Cache, """{"appearance":{"language":"de-DE"}}""");
        var current = Owner().Read();
        Assert.Equal("de-DE", current.Appearance.Language);
        Assert.Equal(FrontendPoints.Theme, current.Appearance.Theme);
        Assert.Equal(FrontendPoints.Monitor.CustomValue, current.Refresh.Monitor.CustomValue);
        Assert.Empty(current.Overlays);
        Assert.Equal(Serialize(current), Serialize(Owner().Read()));
    }

    [Fact]
    public void OverlayChoicesImportBeforeDashboardRoundTripAndResetKeepsOnlyTheChoices()
    {
        WriteLegacy();
        var existing = JsonSerializer.SerializeToNode(new FrontendPreferences
        {
            Appearance = new() { Language = "de-DE" },
            Overlays = new Dictionary<string, FrontendOverlayPresentation>
            { ["game"] = new() { FontSizePx = 22 } }
        }, FrontendPreferencesOwner.Json)!.AsObject();
        existing.Remove("overlaySelectionVersion");
        existing["overlays"]!["game"]!.AsObject().Remove("enabled");
        existing["overlays"]!["game"]!.AsObject().Remove("mode");
        File.WriteAllText(Cache, existing.ToJsonString(FrontendPreferencesOwner.Json));
        var dashboard = Path.Combine(root, "dashboard-settings.json");
        File.WriteAllText(dashboard, """{"cards":[{"id":"existing-layout","main":"cpu"}]}""");
        var owner = new FrontendPreferencesOwner(Cache, App, Overlay, dashboard);
        var current = owner.Read();
        Assert.Equal("de-DE", current.Appearance.Language);
        Assert.Equal("existing-layout", current.Dashboard!.Value.GetProperty("cards")[0].GetProperty("id").GetString());
        Assert.True(current.Overlays["game"].Enabled);
        Assert.Equal("injected", current.Overlays["game"].Mode);
        Assert.Equal(22, current.Overlays["game"].FontSizePx);
        var expected = new FrontendPreferences { Overlays = new Dictionary<string, FrontendOverlayPresentation>
            { ["game"] = new() { Enabled = true, Mode = "injected" } } };
        Assert.Equal(Serialize(expected), Serialize(owner.Reset()));
        Assert.Equal(Serialize(expected), Serialize(new FrontendPreferencesOwner(Cache, App, Overlay, dashboard).Read()));
    }

    [Fact]
    public void ExplicitSaveBeforeFirstReadPreventsBothLegacyImports()
    {
        WriteLegacy();
        var dashboard = Path.Combine(root, "dashboard-settings.json");
        File.WriteAllText(dashboard, """{"cards":[{"id":"old-layout"}]}""");
        var desired = new FrontendPreferences { Appearance = new() { Language = "ja-JP" } };
        var owner = new FrontendPreferencesOwner(Cache, App, Overlay, dashboard);
        owner.Save(desired);
        Assert.Equal(Serialize(desired), Serialize(owner.Read()));
        Assert.Equal(Serialize(desired), Serialize(new FrontendPreferencesOwner(Cache, App, Overlay, dashboard).Read()));
        Assert.Equal(Serialize(new FrontendPreferences()), Serialize(owner.Reset()));
        Assert.False(File.Exists(Cache));
        Assert.Equal(Serialize(new FrontendPreferences()), Serialize(new FrontendPreferencesOwner(Cache, App, Overlay, dashboard).Read()));
        Assert.False(File.Exists(Cache));
    }

    [Fact]
    public void ExplicitNullOverlaysAreRejectedWithoutReplacingTheCommittedCache()
    {
        const string bytes = """{"appearance":{"language":"de-DE"},"overlays":null}""";
        File.WriteAllText(Cache, bytes);
        Assert.Throws<InvalidDataException>(() => Owner().Read());
        Assert.Equal(bytes, File.ReadAllText(Cache));
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
        Assert.Equal(4321, imported.Refresh.Monitor.CustomValue);
        Assert.DoesNotContain("smartMonitoringMode", File.ReadAllText(Cache));
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
    public void CacheFromBeforeTheSmartMonitoringRulingLoadsWithItsChoicesAndLosesOnlyTheRetiredField()
    {
        // The Phase A owner wrote refresh.smartMonitoringMode; the user retired that switch on 2026-10-25.
        var saved = JsonSerializer.SerializeToNode(new FrontendPreferences
        {
            Appearance = new() { Theme = "dark" },
            Refresh = new() { Monitor = new("custom", "balanced", 2345) }
        }, FrontendPreferencesOwner.Json)!.AsObject();
        var refresh = saved["refresh"]!.AsObject();
        refresh.Insert(0, "smartMonitoringMode", "disabled");
        File.WriteAllText(Cache, saved.ToJsonString(FrontendPreferencesOwner.Json));

        var current = Owner().Read();
        Assert.Equal("dark", current.Appearance.Theme);
        Assert.Equal(2345, current.Refresh.Monitor.CustomValue);
        Assert.DoesNotContain("smartMonitoringMode", File.ReadAllText(Cache));
        Assert.Equal(Serialize(current), Serialize(Owner().Read()));
    }

    [Fact]
    public void MotionFeelIsAFrontendChoiceThatSurvivesRestartAndOlderCachesReadTheDefault()
    {
        // 手感是 2026-10-25 加的前端自由点：之前写下的缓存没有这一项，读成点的默认值，其余选择不变。
        var saved = JsonSerializer.SerializeToNode(new FrontendPreferences { Appearance = new() { Theme = "dark" } },
            FrontendPreferencesOwner.Json)!.AsObject();
        saved["appearance"]!.AsObject().Remove("motionFeel");
        File.WriteAllText(Cache, saved.ToJsonString(FrontendPreferencesOwner.Json));
        var owner = Owner();
        Assert.Equal(FrontendPoints.MotionFeel, owner.Read().Appearance.MotionFeel);
        Assert.Equal("dark", owner.Read().Appearance.Theme);

        owner.SaveGlobal(owner.Read() with { Appearance = new() { Theme = "dark", MotionFeel = "smooth" } });
        Assert.Equal("smooth", Owner().Read().Appearance.MotionFeel);
        Assert.Throws<InvalidDataException>(() => owner.Save(new() { Appearance = new() { MotionFeel = "bouncy" } }));
        Assert.Equal("smooth", Owner().Read().Appearance.MotionFeel);
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

    [Fact]
    public void NotificationSectionDefaultsToTheGeneratedPointsAndReadsOldCachesWithoutIt()
    {
        var current = Owner().Read();
        Assert.Equal(FrontendPoints.UpdateInApp, current.Notifications.UpdateInApp);
        Assert.Equal(FrontendPoints.UpdateSystem, current.Notifications.UpdateSystem);
        Assert.Equal(FrontendPoints.AccessRequestSystem, current.Notifications.AccessRequestSystem);
        Assert.Equal("on", current.Notifications.UpdateInApp);
        Assert.Equal("on", current.Notifications.UpdateSystem);
        Assert.Equal("on", current.Notifications.AccessRequestSystem);
        Assert.Null(current.Notifications.UpdateInAppNotifiedVersion);
        // A cache written before the section existed keeps its choices and gets the section's points.
        var saved = JsonSerializer.SerializeToNode(new FrontendPreferences { Appearance = new() { Theme = "dark" } },
            FrontendPreferencesOwner.Json)!.AsObject();
        Assert.True(saved.Remove("notifications"));
        File.WriteAllText(Cache, saved.ToJsonString(FrontendPreferencesOwner.Json));
        var old = Owner().Read();
        Assert.Equal("dark", old.Appearance.Theme);
        Assert.Equal(new FrontendNotifications(), old.Notifications);
    }

    [Fact]
    public void NotificationSectionRoundTripsThroughTheGlobalScope()
    {
        var owner = Owner();
        var desired = owner.Read() with
        {
            Notifications = new() { UpdateInApp = "off", UpdateSystem = "off", AccessRequestSystem = "off", UpdateInAppNotifiedVersion = "0.3.0-beta.20261026.1" }
        };
        owner.SaveGlobal(desired);
        var restarted = Owner().Read();
        Assert.Equal("off", restarted.Notifications.UpdateInApp);
        Assert.Equal("off", restarted.Notifications.UpdateSystem);
        Assert.Equal("off", restarted.Notifications.AccessRequestSystem);
        Assert.Equal("0.3.0-beta.20261026.1", restarted.Notifications.UpdateInAppNotifiedVersion);
        Assert.Contains("\"notifications\"", File.ReadAllText(Cache));
        Assert.Contains("\"accessRequestSystem\"", File.ReadAllText(Cache));
        Assert.Contains("\"updateInAppNotifiedVersion\"", File.ReadAllText(Cache));
    }

    [Theory]
    [InlineData("""{"accessRequestSystem":"yes"}""")]
    [InlineData("""{"accessRequestSystem":""}""")]
    [InlineData("""{"accessRequestSystem":null}""")]
    [InlineData("""{"updateInApp":"yes","updateSystem":"on","updateInAppNotifiedVersion":null}""")]
    [InlineData("""{"updateInApp":"on","updateSystem":"","updateInAppNotifiedVersion":null}""")]
    [InlineData("""{"updateInApp":"on","updateSystem":"on","updateInAppNotifiedVersion":"  "}""")]
    [InlineData("""{"updateInApp":"on","updateSystem":"on","updateInAppNotifiedVersion":"12345678901234567890123456789012345678901234567890123456789012345"}""")]
    [InlineData("""{"updateInApp":"on","updateSystem":"on","notifiedVersion":"1.0.0"}""")]
    [InlineData("null")]
    public void InvalidNotificationSectionIsRejected(string section)
    {
        var owner = Owner();
        owner.Save(new() { Appearance = new() { Theme = "dark" } });
        var node = JsonSerializer.SerializeToNode(owner.Read(), FrontendPreferencesOwner.Json)!.AsObject();
        node["notifications"] = System.Text.Json.Nodes.JsonNode.Parse(section);
        Assert.ThrowsAny<Exception>(() =>
        {
            var value = node.Deserialize<FrontendPreferences>(FrontendPreferencesOwner.Json)!;
            owner.SaveGlobal(value);
        });
        File.WriteAllText(Cache, node.ToJsonString(FrontendPreferencesOwner.Json));
        Assert.ThrowsAny<Exception>(() => Owner().Read());
    }

    [Fact]
    public void OlderNotificationSectionWithoutAccessRequestSwitchUsesItsGeneratedDefault()
    {
        var saved = JsonSerializer.SerializeToNode(new FrontendPreferences
        {
            Notifications = new() { UpdateInApp = "off", UpdateSystem = "off", UpdateInAppNotifiedVersion = "1.2.0" }
        }, FrontendPreferencesOwner.Json)!.AsObject();
        Assert.True(saved["notifications"]!.AsObject().Remove("accessRequestSystem"));
        File.WriteAllText(Cache, saved.ToJsonString(FrontendPreferencesOwner.Json));
        var current = Owner().Read().Notifications;
        Assert.Equal(FrontendPoints.AccessRequestSystem, current.AccessRequestSystem);
        Assert.Equal("off", current.UpdateInApp);
        Assert.Equal("off", current.UpdateSystem);
        Assert.Equal("1.2.0", current.UpdateInAppNotifiedVersion);
    }

    [Fact]
    public void DashboardAndOverlaySavesKeepTheNotificationSectionAndGlobalSavesKeepTheirs()
    {
        var owner = Owner();
        var old = owner.Read();
        owner.SaveGlobal(old with { Notifications = new() { UpdateSystem = "off", AccessRequestSystem = "off", UpdateInAppNotifiedVersion = "1.2.0" } });
        // Stale callers still hold the default section; their scopes must not write it.
        owner.SaveOverlay("game", old with { Overlays = new Dictionary<string, FrontendOverlayPresentation>
            { ["game"] = new() { FontSizePx = 26 } } });
        Assert.Equal("off", owner.Read().Notifications.UpdateSystem);
        Assert.Equal("off", owner.Read().Notifications.AccessRequestSystem);
        Assert.Equal("1.2.0", owner.Read().Notifications.UpdateInAppNotifiedVersion);
        owner.SaveDashboard(old with { Dashboard = null });
        Assert.Equal("off", owner.Read().Notifications.AccessRequestSystem);
        Assert.Equal("off", owner.Read().Notifications.UpdateSystem);
        Assert.Equal("1.2.0", owner.Read().Notifications.UpdateInAppNotifiedVersion);
        owner.SaveGlobal(owner.Read() with { Appearance = new() { Theme = "dark" } });
        Assert.Equal(26, owner.Overlay("game").FontSizePx);
        Assert.Equal("1.2.0", Owner().Read().Notifications.UpdateInAppNotifiedVersion);
        Assert.Equal("dark", Owner().Read().Appearance.Theme);
    }

    [Fact]
    public void ResetRestoresThePreferencePointsButKeepsTheNoticedVersionRecord()
    {
        var owner = Owner();
        owner.SaveGlobal(owner.Read() with
        {
            Appearance = new() { Theme = "dark" },
            Notifications = new() { UpdateInApp = "off", UpdateSystem = "off", AccessRequestSystem = "off", UpdateInAppNotifiedVersion = "1.2.0" }
        });
        var reset = owner.Reset();
        Assert.Equal(FrontendPoints.Theme, reset.Appearance.Theme);
        Assert.Equal(FrontendPoints.UpdateInApp, reset.Notifications.UpdateInApp);
        Assert.Equal(FrontendPoints.UpdateSystem, reset.Notifications.UpdateSystem);
        Assert.Equal(FrontendPoints.AccessRequestSystem, reset.Notifications.AccessRequestSystem);
        Assert.Equal("1.2.0", reset.Notifications.UpdateInAppNotifiedVersion);
        Assert.Equal("1.2.0", Owner().Read().Notifications.UpdateInAppNotifiedVersion);
        Assert.Equal(FrontendPoints.Theme, Owner().Read().Appearance.Theme);
    }

    [Fact]
    public void ResourceBarAndColumnSourcesSurviveTheCacheAndBlankSourcesAreRejected()
    {
        // A bar's or column's source is automatic when absent or null, otherwise a backend process field name.
        static JsonElement Layout(string source) => JsonDocument.Parse($$"""
            {"resourceBars":[{"id":"resource-memory-usage","metricId":"memory.usage","scaleMode":"capacity","source":{{source}}}],
            "resourceTableColumns":[{"id":"memory","visible":true,"source":{{source}}}]}
            """).RootElement.Clone();
        var owner = Owner();
        owner.Save(new() { Dashboard = Layout("\"process_all_private_commit_actual\"") });
        var dashboard = Owner().Read().Dashboard!.Value;
        Assert.Equal("process_all_private_commit_actual",
            dashboard.GetProperty("resourceBars")[0].GetProperty("source").GetString());
        Assert.Equal("process_all_private_commit_actual",
            dashboard.GetProperty("resourceTableColumns")[0].GetProperty("source").GetString());
        owner.Save(new() { Dashboard = Layout("null") });
        foreach (var invalid in new[] { "\"\"", "\"  \"", "42" })
            Assert.Throws<InvalidDataException>(() => owner.Save(new() { Dashboard = Layout(invalid) }));
        Assert.Equal(JsonValueKind.Null, Owner().Read().Dashboard!.Value.GetProperty("resourceBars")[0].GetProperty("source").ValueKind);
    }

    [Fact]
    public void OverlayMetricSourcesSurviveTheCacheAndMustBelongToAnItem()
    {
        // An overlay item's chosen source is frontend data next to the item order; absent means automatic.
        static FrontendPreferences WithSources(Dictionary<string, FrontendOverlayMetricSource> sources) => new()
        {
            Overlays = new Dictionary<string, FrontendOverlayPresentation>
                { ["game"] = new() { Metrics = ["target.fps", "cpu.temperature"], MetricSources = sources } }
        };
        var owner = Owner();
        owner.SaveOverlay("game", WithSources(new() { ["cpu.temperature"] = new("cpu_amd_tctl_temperature_celsius", "cpu|0") }));
        var saved = Owner().Overlay("game");
        Assert.Equal(new FrontendOverlayMetricSource("cpu_amd_tctl_temperature_celsius", "cpu|0"), saved.SourceFor("cpu.temperature"));
        Assert.Null(saved.SourceFor("target.fps"));
        Assert.Contains("\"metricSources\"", File.ReadAllText(Cache));
        foreach (var invalid in new Dictionary<string, FrontendOverlayMetricSource>[]
        {
            new() { ["gpu.0.temperature"] = new("gpu_amd_edge_temperature_celsius", "gpu|0") },
            new() { ["cpu.temperature"] = new(" ", "cpu|0") },
            new() { ["cpu.temperature"] = new("cpu_amd_tctl_temperature_celsius", "") }
        })
            Assert.Throws<InvalidDataException>(() => owner.SaveOverlay("game", WithSources(invalid)));
        Assert.Equal("cpu|0", Owner().Overlay("game").SourceFor("cpu.temperature")!.DeviceIdentity);
        // A cache written before sources existed reads every item as automatic.
        File.WriteAllText(Cache, """{"overlays":{"old":{"metrics":["target.fps"]}}}""");
        Assert.Empty(Owner().Overlay("old").MetricSources);
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
