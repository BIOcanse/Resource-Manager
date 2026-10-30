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

    [Theory]
    [InlineData("\"preferred\"")]
    [InlineData("\"all\"")]
    [InlineData("\"unknown\"")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    public void RetiredReadingSourceChoiceIsDroppedBeforeStrictCacheValidation(string retiredValue)
    {
        foreach (var oldAnimation in new[] { false, true })
        {
            var expected = new FrontendPreferences
            {
                Appearance = new() { Theme = "dark", Language = "de-DE",
                    AnimationStyle = "smooth" },
                Refresh = new() { Monitor = new("custom", "quiet", 4321) }
            };
            var node = JsonSerializer.SerializeToNode(expected, FrontendPreferencesOwner.Json)!.AsObject();
            var appearance = node["appearance"]!.AsObject();
            appearance["readingSourceMode"] = System.Text.Json.Nodes.JsonNode.Parse(retiredValue);
            if (oldAnimation)
            {
                appearance.Remove("animationStyle");
                appearance["animations"] = "normal";
                appearance["motionFeel"] = "smooth";
            }
            File.WriteAllText(Cache, node.ToJsonString(FrontendPreferencesOwner.Json));
            var owner = Owner();
            var loaded = owner.Read();
            Assert.Equal(Serialize(expected), Serialize(loaded));
            Assert.DoesNotContain("readingSourceMode", File.ReadAllText(Cache));
            owner.Save(loaded);
            Assert.DoesNotContain("readingSourceMode", File.ReadAllText(Cache));
            Assert.Equal(Serialize(expected), Serialize(Owner().Read()));
        }
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
        Assert.DoesNotContain("readingSourceMode", Serialize(imported));
        Assert.DoesNotContain("readingSourceMode", File.ReadAllText(Cache));
        // The legacy "animations":"normal" goes through the animation-style migration: desktop.
        Assert.Equal("desktop", imported.Appearance.AnimationStyle);
        Assert.DoesNotContain("autoDisableAnimations", File.ReadAllText(Cache));
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
    public void AnimationStyleSurvivesRestartAndInvalidStylesAreRejected()
    {
        // 动画风格是 2026-10-26 的前端自由点（docs/animation_style_2026-10-26.md）。
        var owner = Owner();
        Assert.Equal(FrontendPoints.AnimationStyle, owner.Read().Appearance.AnimationStyle);
        owner.SaveGlobal(owner.Read() with { Appearance = new() { AnimationStyle = "terminal" } });
        Assert.Equal("terminal", Owner().Read().Appearance.AnimationStyle);
        Assert.Throws<InvalidDataException>(() => owner.Save(new() { Appearance = new() { AnimationStyle = "normal" } }));
        Assert.Equal("terminal", Owner().Read().Appearance.AnimationStyle);
    }

    [Theory]
    [InlineData("auto", "standard", "desktop")]
    [InlineData("auto", "crisp", "desktop")]
    [InlineData("auto", "smooth", "smooth")]
    [InlineData("auto", null, "desktop")]
    [InlineData("normal", "standard", "desktop")]
    [InlineData("normal", "smooth", "smooth")]
    [InlineData("normal", null, "desktop")]
    [InlineData("none", "smooth", "none")]
    [InlineData("ultra", "crisp", "ultra")]
    public void CacheFromBeforeTheAnimationStyleMigratesOnceAndKeepsItsOtherChoices(
        string animations, string? motionFeel, string style)
    {
        // Migration frontend-preferences-animation-style-v1: auto、normal 的风格按原手感；none、ultra 照搬。
        var saved = JsonSerializer.SerializeToNode(new FrontendPreferences { Appearance = new() { Theme = "dark" } },
            FrontendPreferencesOwner.Json)!.AsObject();
        var appearance = saved["appearance"]!.AsObject();
        appearance.Remove("animationStyle");
        appearance["animations"] = animations;
        if (motionFeel is not null) appearance["motionFeel"] = motionFeel;
        File.WriteAllText(Cache, saved.ToJsonString(FrontendPreferencesOwner.Json));
        var current = Owner().Read();
        Assert.Equal(style, current.Appearance.AnimationStyle);
        Assert.Equal("dark", current.Appearance.Theme);
        var text = File.ReadAllText(Cache);
        Assert.DoesNotContain("\"animations\"", text);
        Assert.DoesNotContain("motionFeel", text);
        Assert.DoesNotContain("autoDisableAnimations", text);
        Assert.Equal(Serialize(current), Serialize(Owner().Read()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemovedAutomaticAnimationSwitchIsDroppedBeforeStrictCacheValidation(bool autoDisableAnimations)
    {
        // Migration frontend-preferences-self-scheduling-options-v1: the user removed RM self-scheduling and the switch it
        // triggered on 2026-10-29. An old cache still holds appearance.autoDisableAnimations; the strict reader would reject
        // it, so it is dropped before the read, the file is written back without it and later saves do not write it.
        var expected = new FrontendPreferences
        {
            Appearance = new() { Theme = "dark", AnimationStyle = "terminal" },
            Refresh = new() { Monitor = new("custom", "quiet", 4321) }
        };
        var saved = JsonSerializer.SerializeToNode(expected, FrontendPreferencesOwner.Json)!.AsObject();
        saved["appearance"]!.AsObject()["autoDisableAnimations"] = autoDisableAnimations;
        File.WriteAllText(Cache, saved.ToJsonString(FrontendPreferencesOwner.Json));
        var owner = Owner();
        var loaded = owner.Read();
        Assert.Equal(Serialize(expected), Serialize(loaded));
        Assert.DoesNotContain("autoDisableAnimations", File.ReadAllText(Cache));
        owner.SaveGlobal(loaded);
        Assert.DoesNotContain("autoDisableAnimations", File.ReadAllText(Cache));
        Assert.Equal(Serialize(expected), Serialize(Owner().Read()));
    }

    [Fact]
    public void StrictReaderRejectsTheRemovedAnimationSwitchWithoutTheReadMigration()
    {
        // Without the read migration the strict reader rejects the field: it is not part of the cache model any more.
        var node = JsonSerializer.SerializeToNode(new FrontendPreferences(), FrontendPreferencesOwner.Json)!.AsObject();
        Assert.False(node["appearance"]!.AsObject().ContainsKey("autoDisableAnimations"));
        node["appearance"]!.AsObject()["autoDisableAnimations"] = true;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FrontendPreferences>(
            node.ToJsonString(FrontendPreferencesOwner.Json), FrontendPreferencesOwner.Json));
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

    private static readonly string[] NotificationFields =
    [
        "updateNotify", "updateInApp", "updateSystem", "accessRequestNotify", "accessRequestInApp", "accessRequestSystem",
        "memoryProtectionNotify", "memoryProtectionInApp", "memoryProtectionSystem"
    ];

    private static FrontendNotifications AllNotifications(string value) => new()
    {
        UpdateNotify = value, UpdateInApp = value, UpdateSystem = value,
        AccessRequestNotify = value, AccessRequestInApp = value, AccessRequestSystem = value,
        MemoryProtectionNotify = value, MemoryProtectionInApp = value, MemoryProtectionSystem = value
    };

    [Fact]
    public void NotificationSectionDefaultsToTheGeneratedPointsAndReadsOldCachesWithoutIt()
    {
        // docs/notifications_2026-10-29.md: three switches per kind (show, Windows, in-app), all on by default.
        var current = Owner().Read().Notifications;
        Assert.Equal(new FrontendNotifications
        {
            UpdateNotify = FrontendPoints.UpdateNotify, UpdateInApp = FrontendPoints.UpdateInApp, UpdateSystem = FrontendPoints.UpdateSystem,
            AccessRequestNotify = FrontendPoints.AccessRequestNotify, AccessRequestInApp = FrontendPoints.AccessRequestInApp,
            AccessRequestSystem = FrontendPoints.AccessRequestSystem, MemoryProtectionNotify = FrontendPoints.MemoryProtectionNotify,
            MemoryProtectionInApp = FrontendPoints.MemoryProtectionInApp, MemoryProtectionSystem = FrontendPoints.MemoryProtectionSystem
        }, current);
        Assert.Equal(AllNotifications("on"), current);
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
        var desired = AllNotifications("off") with { UpdateInApp = "on", AccessRequestSystem = "on", MemoryProtectionNotify = "on" };
        owner.SaveGlobal(owner.Read() with { Notifications = desired });
        Assert.Equal(desired, Owner().Read().Notifications);
        var text = File.ReadAllText(Cache);
        foreach (var field in NotificationFields) Assert.Contains($"\"{field}\"", text);
    }

    [Theory]
    [InlineData("""{"updateNotify":"yes"}""")]
    [InlineData("""{"updateInApp":"yes"}""")]
    [InlineData("""{"updateSystem":""}""")]
    [InlineData("""{"accessRequestNotify":"yes"}""")]
    [InlineData("""{"accessRequestInApp":null}""")]
    [InlineData("""{"accessRequestSystem":"yes"}""")]
    [InlineData("""{"memoryProtectionNotify":""}""")]
    [InlineData("""{"memoryProtectionInApp":"yes"}""")]
    [InlineData("""{"memoryProtectionSystem":null}""")]
    [InlineData("""{"updateInApp":"on","notifiedVersion":"1.0.0"}""")]
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
    public void OlderNotificationSectionKeepsItsChannelChoicesGetsTheNewSwitchesAndDropsTheNoticedVersion()
    {
        // Before 2026-10-29 the section had updateInApp, updateSystem, accessRequestSystem, memoryProtectionSystem and the
        // in-app notice's noticed version. The four switches are the same points as the new channels; the record is
        // dropped once (migration frontend-preferences-notified-version-v1) and the cache is written back without it.
        var saved = JsonSerializer.SerializeToNode(new FrontendPreferences(), FrontendPreferencesOwner.Json)!.AsObject();
        saved["notifications"] = System.Text.Json.Nodes.JsonNode.Parse(
            """{"updateInApp":"off","updateSystem":"off","accessRequestSystem":"off","memoryProtectionSystem":"off","updateInAppNotifiedVersion":"1.2.0"}""");
        File.WriteAllText(Cache, saved.ToJsonString(FrontendPreferencesOwner.Json));
        var current = Owner().Read().Notifications;
        Assert.Equal(new FrontendNotifications
        {
            UpdateInApp = "off", UpdateSystem = "off", AccessRequestSystem = "off", MemoryProtectionSystem = "off"
        }, current);
        Assert.Equal(FrontendPoints.UpdateNotify, current.UpdateNotify);
        Assert.Equal(FrontendPoints.AccessRequestInApp, current.AccessRequestInApp);
        Assert.DoesNotContain("updateInAppNotifiedVersion", File.ReadAllText(Cache));
        Assert.Equal(current, Owner().Read().Notifications);
    }

    [Fact]
    public void DashboardAndOverlaySavesKeepTheNotificationSectionAndGlobalSavesKeepTheirs()
    {
        var owner = Owner();
        var old = owner.Read();
        var chosen = new FrontendNotifications { UpdateSystem = "off", AccessRequestNotify = "off", MemoryProtectionInApp = "off" };
        owner.SaveGlobal(old with { Notifications = chosen });
        // Stale callers still hold the default section; their scopes must not write it.
        owner.SaveOverlay("game", old with { Overlays = new Dictionary<string, FrontendOverlayPresentation>
            { ["game"] = new() { FontSizePx = 26 } } });
        Assert.Equal(chosen, owner.Read().Notifications);
        owner.SaveDashboard(old with { Dashboard = null });
        Assert.Equal(chosen, owner.Read().Notifications);
        owner.SaveGlobal(owner.Read() with { Appearance = new() { Theme = "dark" } });
        Assert.Equal(26, owner.Overlay("game").FontSizePx);
        Assert.Equal(chosen, Owner().Read().Notifications);
        Assert.Equal("dark", Owner().Read().Appearance.Theme);
    }

    [Fact]
    public void ResetRestoresEveryNotificationSwitchToItsPoint()
    {
        var owner = Owner();
        owner.SaveGlobal(owner.Read() with { Appearance = new() { Theme = "dark" }, Notifications = AllNotifications("off") });
        var reset = owner.Reset();
        Assert.Equal(FrontendPoints.Theme, reset.Appearance.Theme);
        Assert.Equal(new FrontendNotifications(), reset.Notifications);
        Assert.Equal(new FrontendNotifications(), Owner().Read().Notifications);
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
    public void DashboardCardSlotsSurviveTheCacheWithinTheUnitsACardCanHold()
    {
        // A card is a list of slots, each a metric and a size of 1, 2, 3 or all units; the legacy main/small layout is still read.
        static JsonElement Layout(string card) => JsonDocument.Parse($$"""{"cards":[{{card}}]}""").RootElement.Clone();
        var owner = Owner();
        Assert.Equal(6, FrontendPoints.DashboardLayoutLimits.CardUnits);
        owner.Save(new() { Dashboard = Layout("""{"id":"cpu","slots":[{"metricId":"cpu.usage","size":"medium"},{"metricId":"npu_all_engine_busy_percent@npu|0","size":"large","binding":{"scopeKind":"device-field","scopeKey":"npu|0"}},{"metricId":"cpu.frequency","size":"small","binding":null}]}""") });
        var slots = Owner().Read().Dashboard!.Value.GetProperty("cards")[0].GetProperty("slots");
        Assert.Equal(new string?[] { "medium", "large", "small" }, slots.EnumerateArray().Select(slot => slot.GetProperty("size").GetString()).ToArray());
        Assert.Equal("npu|0", slots[1].GetProperty("binding").GetProperty("scopeKey").GetString());
        owner.Save(new() { Dashboard = Layout("""{"id":"wide","slots":[{"metricId":"gpu.0.usage","size":"full"}]}""") });
        // Migrated from the legacy layout: a main reading (2 units) and six rows is the most a card can still hold.
        owner.Save(new() { Dashboard = Layout("""{"id":"old","slots":[{"metricId":"a","size":"medium"},{"metricId":"b","size":"small"},{"metricId":"c","size":"small"},{"metricId":"d","size":"small"},{"metricId":"e","size":"small"},{"metricId":"f","size":"small"},{"metricId":"g","size":"small"}]}""") });
        owner.Save(new() { Dashboard = Layout("""{"id":"legacy","main":"cpu.usage","small":["cpu.frequency"]}""") });
        foreach (var invalid in new[]
        {
            """{"id":"x","slots":[{"metricId":"cpu.usage","size":"huge"}]}""",
            """{"id":"x","slots":[{"metricId":"cpu.usage"}]}""",
            """{"id":"x","slots":[{"metricId":"  ","size":"small"}]}""",
            """{"id":"x","slots":[{"metricId":"cpu.usage","size":"small","binding":"gpu"}]}""",
            """{"id":"x","slots":{"metricId":"cpu.usage","size":"small"}}""",
            """{"id":"x","slots":[{"metricId":"a","size":"full"},{"metricId":"b","size":"large"}]}"""
        })
            Assert.Throws<InvalidDataException>(() => owner.Save(new() { Dashboard = Layout(invalid) }));
        Assert.Equal("legacy", Owner().Read().Dashboard!.Value.GetProperty("cards")[0].GetProperty("id").GetString());
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

    [Fact]
    public void RenamedNvmlFbUsedFieldIsMigratedInCardSlotsAndOverlaySources()
    {
        // Migration frontend-preferences-nvml-fb-used-v1: only the retired v2 name changes; the v1 field,
        // metric ids and every other value are left as they were.
        const string v1 = "gpu_nvidia_fb_used_v1_including_reserved_bytes";
        static string Preferences(string field) => """
            {"dashboard":{"version":17,"cards":[{"id":"gpu-card","slots":[
              {"metricId":"<field>@gpu:a","size":"medium","binding":{"scopeKind":"device-field","scopeKey":"gpu:a"}},
              {"metricId":"<v1>@gpu:a","size":"small","binding":{"scopeKind":"device-field","scopeKey":"gpu:a"}},
              {"metricId":"gpu.0.nvmlFbUsedBytes","size":"small","binding":{"scopeKind":"gpu","scopeKey":"gpu:a"}}]}],
              "resourceBars":[{"id":"gpu-bar","metricId":"gpu.0.vram","scaleMode":"capacity"}]},
             "overlays":{"game":{"enabled":true,"metrics":["gpu.vram","gpu.vramV1"],"metricSources":{
              "gpu.vram":{"fieldName":"<field>","deviceIdentity":"gpu:a"},
              "gpu.vramV1":{"fieldName":"<v1>","deviceIdentity":"gpu:a"}}}}}
            """.Replace("<field>", field).Replace("<v1>", v1);
        File.WriteAllText(Cache, Preferences(FrontendPreferencesOwner.RetiredNvmlFbUsedField));
        var expectedPath = Path.Combine(root, "expected.json");
        File.WriteAllText(expectedPath, Preferences(FrontendPreferencesOwner.NvmlFbUsedField));
        var expected = Serialize(new FrontendPreferencesOwner(expectedPath).Read());

        var loaded = Owner().Read();
        Assert.Equal(expected, Serialize(loaded));
        Assert.Equal(FrontendPreferencesOwner.NvmlFbUsedField, loaded.Overlays["game"].SourceFor("gpu.vram")!.FieldName);
        var written = File.ReadAllText(Cache);
        Assert.DoesNotContain(FrontendPreferencesOwner.RetiredNvmlFbUsedField, written);
        Assert.Contains(v1, written);
        Assert.Equal(expected, Serialize(Owner().Read()));
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
