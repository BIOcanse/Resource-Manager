using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.Settings;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

/// <summary>The backend settings section <c>performanceOverlay</c> and the toggle that writes it.</summary>
public sealed class PerformanceOverlaySettingsTests
{
    [Fact]
    public async Task GenericSelectionPersistsWithoutRenderingPolicy()
    {
        using var files = new StartupModuleTestCatalog();
        var store = new JsonStartupModuleSettingsStore(new BackendStateItems().Authority, files, files.Catalog);
        Assert.Empty((await store.GetAsync(default)).Software);
        await store.SaveAsync(new("software", ["fixture-module"]), default);
        var saved = Assert.Single((await new JsonStartupModuleSettingsStore(new BackendStateItems().Authority, files, files.Catalog).GetAsync(default)).Software);
        Assert.Equal(["fixture-module"], saved.ModuleIds);
        var path = Path.Combine(files.Root, "UserData", "SoftwareProfiles", "startup-modules.local.json");
        var bytes = await File.ReadAllTextAsync(path);
        foreach (var presentation in new[] { "enabled", "mode", "metrics", "fontSizePx", "overlay" })
            Assert.DoesNotContain(presentation, bytes, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new("software", ["not-admitted"]), default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new("software", ["fixture-module", "fixture-module"]), default));
    }

    [Fact]
    public void ExactManifestImagesAreProtectedRegardlessOfBasename()
    {
        using var files = new StartupModuleTestCatalog();
        var image = Path.Combine(files.Root, "Bin", "Tools", "renamed-client.exe");
        File.WriteAllBytes(image, [4, 5, 6]);
        files.WriteRelease("Bin/Tools/renamed-client.exe");
        var catalog = PackageRuntimeCatalog.Load(files.Root);
        Assert.True(catalog.IsInfrastructureImage(image));
        Assert.True(catalog.MayBeInfrastructureImageName("RENAMED-CLIENT"));
        Assert.False(catalog.MayBeInfrastructureImageName("unlisted-client"));
        Assert.False(catalog.IsInfrastructureImage(Path.Combine(files.Root, "elsewhere", "renamed-client.exe")));
        Assert.Single(catalog.StartupModules);
        File.AppendAllText(files.Image, "corrupt");
        Assert.Throws<InvalidDataException>(() => PackageRuntimeCatalog.Load(files.Root));
    }

    [Fact]
    public void CapabilityAndIfeoEligibilityConsumeTheSameExactImageAdmission()
    {
        using var files = new StartupModuleTestCatalog();
        var image = Path.Combine(files.Root, "Bin", "Tools", "renamed.exe");
        var outside = Path.Combine(Directory.CreateDirectory(Path.Combine(files.Root, "Outside")).FullName, "renamed.exe");
        File.Copy(Environment.ProcessPath!, image);
        File.Copy(Environment.ProcessPath!, outside);
        var broker = Path.Combine(files.Root, "broker.exe");
        File.WriteAllBytes(broker, []);
        files.WriteRelease("Bin/Tools/renamed.exe");
        var catalog = PackageRuntimeCatalog.Load(files.Root);
        var reader = new WindowsGpuPlacementCapabilityReader(files.Root, catalog);
        Assert.Equal("self-process-blocked", reader.Evaluate("inside", image, "x64", null).Startup.ReasonCode);
        Assert.NotEqual("self-process-blocked", reader.Evaluate("outside", outside, "x64", null).Startup.ReasonCode);
        var registry = new WindowsIfeoGpuLaunchInterceptionRegistry(broker, new NeverReadRegistry(), catalog);
        Assert.False(registry.EvaluateEligibility(image, broker).Eligible);
        Assert.False(registry.EvaluateEligibility(broker, broker).Eligible);
        Assert.True(registry.EvaluateEligibility(outside, broker).Eligible);
    }

    private sealed class NeverReadRegistry : IIfeoOwnedRuleStore
    {
        public IReadOnlyList<string> EnumerateImageNames(Microsoft.Win32.RegistryView view) => throw new InvalidOperationException();
        public IfeoImageSnapshot? ReadImage(Microsoft.Win32.RegistryView view, string imageName) => throw new InvalidOperationException();
        public int DeleteOwnedRules(Microsoft.Win32.RegistryView view, string imageName, IReadOnlyList<IfeoOwnedRuleCandidate> candidates, bool settleParentOwnership)
            => throw new InvalidOperationException();
    }

    [Fact]
    public void MissingManifestProtectsOnlyActualHostAndFinalImageManifestIsSupported()
    {
        using var files = new StartupModuleTestCatalog();
        File.Move(Path.Combine(files.Root, "release-manifest.json"), Path.Combine(files.Root, "resource-manager-final-image.manifest.json"));
        Assert.Single(PackageRuntimeCatalog.Load(files.Root).StartupModules);
        File.Delete(Path.Combine(files.Root, "resource-manager-final-image.manifest.json"));
        var catalog = PackageRuntimeCatalog.Load(files.Root, files.Image);
        Assert.Empty(catalog.StartupModules);
        Assert.True(catalog.IsInfrastructureImage(files.Image));
    }

    private static PerformanceOverlayDefaults Defaults => AppSettingsDefaults.PerformanceOverlay;

    [Fact]
    public void DeclaredItemWireNamesPreserveEveryChoiceUnderTheStrictCompilerSerializer()
    {
        var item = JsonSerializer.Deserialize<PerformanceOverlayItemOptions>(
            """{"kind":"frames","id":"frames.fps","source":"dxgi","intervalMs":321,"syncPrecision":"high"}""",
            FreedomPointRegistry.CreateSerializerOptions())!;
        Assert.Equal("frames", item.Kind);
        Assert.Equal("frames.fps", item.Id);
        Assert.Equal("dxgi", item.Source);
        Assert.Equal(321, item.IntervalMs);
        Assert.Equal("high", item.SyncPrecision);
    }

    [Fact]
    public void DefaultSettingsHaveNoOverlayAndAnOffToggleHotkey()
    {
        var settings = AppSettingsDefaults.Create();
        Assert.Empty(settings.PerformanceOverlay!.Software);
        var hotkey = Assert.Single(settings.SystemIntegration.Hotkeys,
            static item => item.ActionId == AppSystemIntegrationActionIds.TogglePerformanceOverlayForeground);
        Assert.False(hotkey.Enabled);
        Assert.False(Defaults.Enabled);
        Assert.Equal(AppPerformanceOverlayModes.External, Defaults.Mode);
    }

    [Fact]
    public void NormalizerTrimsIdsAndCompletesEntriesFromTheDeclaredDefaults()
    {
        var settings = AppSettingsDefaults.Create() with
        {
            PerformanceOverlay = new(new Dictionary<string, AppPerformanceOverlaySoftwareSettings>
            {
                [" steam:570 "] = new(Enabled: true, FontSizePx: 20),
                ["local:game"] = new(Mode: AppPerformanceOverlayModes.Injected)
            })
        };

        var overlay = AppSettingsNormalizer.Normalize(settings).PerformanceOverlay!;

        Assert.Equal(2, overlay.Software.Count);
        var first = overlay.Software["steam:570"];
        Assert.True(first.Enabled);
        Assert.Equal(20, first.FontSizePx);
        Assert.Equal(Defaults.Mode, first.Mode);
        Assert.Equal(Defaults.SizeMode, first.SizeMode);
        Assert.Equal(Defaults.Anchor, first.Anchor);
        Assert.Equal(Defaults.MarginPx, first.MarginPx);
        Assert.Equal(Defaults.RegionWidthRatio, first.RegionWidthRatio);
        Assert.Equal(Defaults.Items.Count, first.Items!.Count);
        Assert.True(overlay.Software.ContainsKey("STEAM:570"));
        Assert.Equal(Defaults.Enabled, overlay.Software["local:game"].Enabled);
        Assert.Null(Defaults.Validate(overlay));
    }

    [Theory]
    [InlineData("{\"\":{\"enabled\":true}}")]
    [InlineData("{\"a\":null}")]
    [InlineData("{\"a\":{},\"A\":{}}")]
    [InlineData("{\"a\":{},\" a \":{}}")]
    [InlineData("null")]
    public void NormalizationDoesNotDiscardMalformedSoftwareMaps(string json)
    {
        var software = JsonSerializer.Deserialize<Dictionary<string, AppPerformanceOverlaySoftwareSettings>>(json, JsonSerializerOptions.Web);
        var normalized = AppSettingsNormalizer.NormalizePerformanceOverlay(new(software!));
        Assert.NotNull(Defaults.Validate(normalized));
        Assert.Same(software, normalized.Software);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"kind\":\"software\",\"id\":\"cpu.usage\",\"source\":null}]")]
    [InlineData("[{\"kind\":\"software\",\"id\":\"cpu.usage\",\"syncPrecision\":null}]")]
    public void NormalizationPreservesInvalidItemsForTheWriteValidator(string json)
    {
        var items = JsonSerializer.Deserialize<TargetedRecordingItem[]>(json, JsonSerializerOptions.Web)!;
        var normalized = AppSettingsNormalizer.NormalizePerformanceOverlay(new(new Dictionary<string, AppPerformanceOverlaySoftwareSettings>
        {
            ["game"] = new(Items: items)
        }));
        Assert.Equal("performance-overlay-invalid-item", Defaults.Validate(normalized));
        Assert.Equal(items.Length, normalized.Software["game"].Items!.Count);
    }

    [Theory]
    [InlineData("mode", "\"floating\"", "performance-overlay-invalid-mode")]
    [InlineData("sizeMode", "\"percent\"", "performance-overlay-invalid-size-mode")]
    [InlineData("anchor", "\"center\"", "performance-overlay-invalid-anchor")]
    [InlineData("fontSizePx", "4", "performance-overlay-font-size-out-of-range")]
    [InlineData("marginPx", "-1", "performance-overlay-margin-out-of-range")]
    [InlineData("regionWidthRatio", "1.5", "performance-overlay-region-out-of-range")]
    [InlineData("items", "[{\"kind\":\"software\",\"id\":\"cpu.usage\"},{\"kind\":\"software\",\"id\":\"cpu.usage\"}]", "performance-overlay-duplicate-item")]
    public void ValidatorRejectsOutOfContractValues(string field, string value, string reason)
    {
        var entry = JsonNode.Parse(JsonSerializer.Serialize(Defaults.Entry(), JsonSerializerOptions.Web))!.AsObject();
        entry[field] = JsonNode.Parse(value);
        var parsed = entry.Deserialize<AppPerformanceOverlaySoftwareSettings>(JsonSerializerOptions.Web)!;

        Assert.Equal(reason, Defaults.Validate(new(new Dictionary<string, AppPerformanceOverlaySoftwareSettings> { ["a"] = parsed })));
        Assert.Equal("performance-overlay-incomplete", Defaults.ValidateEntry(new AppPerformanceOverlaySoftwareSettings(Enabled: true)));
    }

    [Fact]
    public void ValidatorLimitsTheNumberOfSoftware()
    {
        var many = Enumerable.Range(0, Defaults.MaximumSoftware + 1)
            .ToDictionary(static index => $"s{index}", _ => Defaults.Entry());
        Assert.Equal("performance-overlay-too-many-software", Defaults.Validate(new(many)));
        many.Remove("s0");
        Assert.Null(Defaults.Validate(new(many)));
    }

    [Fact]
    public void MigratorReadsAMissingSectionAsNoOverlayAndKeepsStoredEntries()
    {
        var root = JsonNode.Parse(JsonSerializer.Serialize(AppSettingsDefaults.Create(), JsonSerializerOptions.Web))!.AsObject();
        root.Remove("performanceOverlay");
        using (var missing = JsonDocument.Parse(root.ToJsonString()))
            Assert.Empty(AppSettingsMigrator.MigrateForRead(missing.RootElement).PerformanceOverlay?.Software ?? new Dictionary<string, AppPerformanceOverlaySoftwareSettings>());

        root["performanceOverlay"] = JsonNode.Parse("""{"software":{"steam:570":{"enabled":true,"fontSizePx":18}}}""");
        using var present = JsonDocument.Parse(root.ToJsonString());
        var entry = AppSettingsNormalizer.Normalize(AppSettingsMigrator.MigrateForRead(present.RootElement)).PerformanceOverlay!.Software["steam:570"];
        Assert.True(entry.Enabled);
        Assert.Equal(18, entry.FontSizePx);
        Assert.Equal(Defaults.Mode, entry.Mode);
    }

    [Fact]
    public void PatchReplacesAnEntryWholeAddsNewIdsAndRemovesWithNull()
    {
        var current = AppSettingsNormalizer.Normalize(AppSettingsDefaults.Create() with
        {
            PerformanceOverlay = new(new Dictionary<string, AppPerformanceOverlaySoftwareSettings>
            {
                ["steam:570"] = new(Enabled: true, FontSizePx: 30),
                ["local:old"] = new(Enabled: true),
                ["untouched"] = new(Enabled: true, FontSizePx: 28)
            })
        });
        using var changes = JsonDocument.Parse("""{"performanceOverlay":{"software":{"STEAM:570":{"enabled":false},"local:new":{"enabled":true},"local:old":null}}}""");

        var overlay = AppSettingsNormalizer.Normalize(AppSettingsPatchApplier.Apply(current, changes.RootElement)).PerformanceOverlay!;

        // The writer's spelling of a matched id replaces the stored one with the entry.
        Assert.Equal(["STEAM:570", "local:new", "untouched"], overlay.Software.Keys.Order(StringComparer.Ordinal));
        var replaced = overlay.Software["steam:570"];
        Assert.False(replaced.Enabled);
        // The entry is replaced whole: the stored font size does not survive an object without it.
        Assert.Equal(Defaults.FontSizePx, replaced.FontSizePx);
        Assert.True(overlay.Software["local:new"].Enabled);
        Assert.Equal(JsonSerializer.Serialize(current.PerformanceOverlay!.Software["untouched"], JsonSerializerOptions.Web),
            JsonSerializer.Serialize(overlay.Software["untouched"], JsonSerializerOptions.Web));
    }

    [Theory]
    [InlineData("{\"software\":null}")]
    [InlineData("{\"software\":[]}")]
    [InlineData("{\"software\":{\" \":{}}}")]
    [InlineData("{\"software\":{\"a\":{},\"A\":{}}}")]
    [InlineData("{\"software\":{\"a\":{},\"a\":{}}}")]
    [InlineData("{\"software\":{\"a\":{\"enabeld\":true}}}")]
    [InlineData("{\"software\":{\"a\":{\"enabled\":true,\"enabled\":false}}}")]
    public void PatchRejectsMalformedOverlayInsteadOfLosingInput(string section)
    {
        using var changes = JsonDocument.Parse("{\"performanceOverlay\":" + section + "}");
        Assert.Throws<AppSettingsPatchException>(() => AppSettingsPatchApplier.Apply(AppSettingsDefaults.Create(), changes.RootElement));
    }

    [Fact]
    public void PatchMatchesTrimmedIdsBeforeReplacingOrDeleting()
    {
        var current = AppSettingsNormalizer.Normalize(AppSettingsDefaults.Create() with
        {
            PerformanceOverlay = new(new Dictionary<string, AppPerformanceOverlaySoftwareSettings> { ["Game"] = new(Enabled: true) })
        });
        using var changes = JsonDocument.Parse("""{"performanceOverlay":{"software":{" game ":null}}}""");
        Assert.Empty(AppSettingsPatchApplier.Apply(current, changes.RootElement).PerformanceOverlay!.Software);
    }

    [Fact]
    public async Task ToggleWithoutAnIdInvertsTheForegroundSoftwaresSwitch()
    {
        var store = new Store();
        var (receipt, rejection) = await Toggle(store, new(), ForegroundSoftwareResolution.Found("steam:570", "Dota 2"));

        Assert.Null(rejection);
        Assert.Equal(new PerformanceOverlayToggleReceipt("steam:570", "Dota 2", true, store.Result.Revision)
        { RuntimeApplicationDisposition = AppSettingsRuntimeApplicationDisposition.CommittedAndApplied }, receipt);
        Assert.True(store.Settings.PerformanceOverlay!.Software["steam:570"].Enabled);

        (receipt, _) = await Toggle(store, null, ForegroundSoftwareResolution.Found("steam:570", "Dota 2"));
        Assert.False(receipt!.Enabled);
        Assert.False(store.Settings.PerformanceOverlay!.Software["steam:570"].Enabled);
    }

    [Fact]
    public async Task ToggleWithoutAnIdAndWithoutAttributableForegroundChangesNothing()
    {
        var store = new Store();
        var (receipt, rejection) = await Toggle(store, new(),
            ForegroundSoftwareResolution.Rejected(PerformanceOverlayRejections.ForegroundNotAttributed));

        Assert.Null(receipt);
        Assert.Equal(PerformanceOverlayRejections.ForegroundNotAttributed, rejection);
        Assert.Equal(0, store.Patches);
    }

    [Fact]
    public async Task ToggleWithAnIdUsesTheStoredSpellingAndKeepsTheEntrysOtherFields()
    {
        var store = new Store(new Dictionary<string, AppPerformanceOverlaySoftwareSettings> { ["Steam:570"] = new(Enabled: false, FontSizePx: 30) });
        var (receipt, rejection) = await Toggle(store, new(" steam:570 "), null);

        Assert.Null(rejection);
        Assert.Equal("Steam:570", receipt!.SoftwareId);
        Assert.Null(receipt.SoftwareName);
        Assert.True(receipt.Enabled);
        var entry = Assert.Single(store.Settings.PerformanceOverlay!.Software).Value;
        Assert.True(entry.Enabled);
        Assert.Equal(30, entry.FontSizePx);
    }

    [Fact]
    public async Task ToggleWithAnExplicitValueSetsItInsteadOfInverting()
    {
        var store = new Store(new Dictionary<string, AppPerformanceOverlaySoftwareSettings> { ["steam:570"] = new(Enabled: true) });
        var (receipt, _) = await Toggle(store, new("steam:570", true), null);
        Assert.True(receipt!.Enabled);
        Assert.True(store.Settings.PerformanceOverlay!.Software["steam:570"].Enabled);

        (receipt, _) = await Toggle(store, new(Enabled: false), ForegroundSoftwareResolution.Found("local:new", "New"));
        Assert.Equal("local:new", receipt!.SoftwareId);
        Assert.False(receipt.Enabled);
        Assert.False(store.Settings.PerformanceOverlay!.Software["local:new"].Enabled);
    }

    [Fact]
    public async Task ToggleReportsARejectedWriteWithItsReason()
    {
        var store = new Store { RejectWith = "performance-overlay-too-many-software" };
        var (receipt, rejection) = await Toggle(store, new("steam:570"), null);
        Assert.Null(receipt);
        Assert.Equal("performance-overlay-too-many-software", rejection);
    }

    [Fact]
    public async Task ConcurrentTogglesUseTheRevisionGateWithoutReplayingAnInversion()
    {
        var store = new Store();
        var bothLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        async Task<AppSettingsUpdateResult> Load(CancellationToken token)
        {
            var snapshot = store.Result;
            if (Interlocked.Increment(ref loads) == 2) bothLoaded.SetResult();
            await bothLoaded.Task.WaitAsync(token);
            return snapshot;
        }
        Task<(PerformanceOverlayToggleReceipt? Receipt, string? Rejection)> Act() => PerformanceOverlayToggle.ToggleAsync(
            new("game"), _ => throw new InvalidOperationException(), Load, store.PatchAsync, default);

        var results = await Task.WhenAll(Act(), Act());

        Assert.Single(results, static result => result.Receipt is not null);
        Assert.Single(results, static result => result.Rejection == "settings-revision-conflict");
        Assert.Equal(2, loads);
        Assert.True(store.Settings.PerformanceOverlay!.Software["game"].Enabled);
    }

    [Fact]
    public async Task ForegroundUsesSeparateCurrentAttributionAndRejectsPidReuseWithoutSampling()
    {
        var inventory = new SchedulingProcessFact(42, 101, "game", null, "wrong-inventory-owner", "wrong",
            SoftwareKinds.Unattributed, "", 0, SchedulingProcessMetricMask.None, 0, 0, 1, []);
        var attribution = inventory with { SoftwareId = "game", SoftwareName = "Game", SoftwareKind = SoftwareKinds.RuntimeRoot };
        var snapshot = new SchedulingProcessFactSnapshot(SamplingObservationStatus.Current, 1, 1, 1, 0, 0, 0,
            SchedulingProcessMetricMask.None, SchedulingProcessMetricMask.None, [])
        {
            HasSeparateFoundationPayloads = true,
            InventoryProcesses = [inventory], AttributionProcesses = [attribution],
            FoundationDatasetObservations = new Dictionary<string, SchedulingProcessFoundationDatasetObservation>
            {
                [SamplingDatasetIds.ProcessInventory] = SchedulingProcessFoundationDatasetObservation.CreateCurrent(SamplingDatasetIds.ProcessInventory, 1, 1),
                [SamplingDatasetIds.ProcessAttribution] = SchedulingProcessFoundationDatasetObservation.CreateCurrent(SamplingDatasetIds.ProcessAttribution, 1, 1)
            }
        };
        var source = new PublishedSource(snapshot);
        Assert.Equal(ForegroundSoftwareResolution.Found("game", "Game"), PerformanceOverlayToggle.ResolvePublishedSoftware(source, 42, 101));
        Assert.Equal(PerformanceOverlayRejections.ForegroundProcessNotFound,
            PerformanceOverlayToggle.ResolvePublishedSoftware(source, 42, 102).Reason);
        source.Current = snapshot with { AttributionProcesses = [attribution with { ProcessStartKey = 102 }] };
        Assert.Equal(PerformanceOverlayRejections.ForegroundNotAttributed,
            PerformanceOverlayToggle.ResolvePublishedSoftware(source, 42, 101).Reason);
        source.Current = snapshot with { FoundationDatasetObservations = new Dictionary<string, SchedulingProcessFoundationDatasetObservation>() };
        Assert.Equal(PerformanceOverlayRejections.ProcessInventoryUnavailable,
            PerformanceOverlayToggle.ResolvePublishedSoftware(source, 42, 101).Reason);
        source.Current = snapshot;
        await using var app = Endpoints(new Store(), StartupCapabilitySet.Full, source, new FailIfSampled());
        var result = await Invoke(app, "/api/performance-overlay/foreground", "GET", query: "?processId=42&processStartKey=101");
        Assert.Equal(200, result.Status);
        using var json = JsonDocument.Parse(result.Body);
        Assert.Equal("game", json.RootElement.GetProperty("softwareId").GetString());
    }

    [Fact]
    public void EditingConstraintsAreAReadOnlyProjectionOfTheSelectedBackendDefaults()
    {
        var selected = Defaults with { MaximumItems = 7, FontSizePxMinimum = 9, FontSizePxMaximum = 57,
            MarginPxMinimum = 2, MarginPxMaximum = 97, RegionRatioMinimum = 0.1, RegionRatioMaximum = 0.85, RegionRatioStep = 0.025 };
        Assert.Equal(new PerformanceOverlayEditingConstraints(7, 9, 57, 2, 97, 0.1, 0.85, 0.025),
            PerformanceOverlayEditingConstraints.FromDefaults(selected));
        Assert.Equal(Defaults.MaximumItems, PerformanceOverlayEditingConstraints.FromDefaults(Defaults).MaximumItems);
    }

    [Fact]
    public async Task SoftwareGetAndToggleUseTheProductionJsonContextAndSavedDisposition()
    {
        var store = new Store(new Dictionary<string, AppPerformanceOverlaySoftwareSettings>
        {
            ["Game"] = new(Enabled: false, FontSizePx: 24), ["other"] = new(Enabled: true)
        }) { CommitDisposition = "savedNotApplied" };
        await using var app = Endpoints(store, StartupCapabilitySet.Full);
        var get = await Invoke(app, "/api/performance-overlay/software/{softwareId}", "GET", "game");
        using (var document = JsonDocument.Parse(get.Body))
        {
            Assert.Equal(200, get.Status);
            Assert.Equal("Game", document.RootElement.GetProperty("softwareId").GetString());
            Assert.Equal(24, document.RootElement.GetProperty("settings").GetProperty("fontSizePx").GetInt32());
            Assert.Equal(store.Result.Revision, document.RootElement.GetProperty("revision").GetString());
            var constraints = document.RootElement.GetProperty("editingConstraints");
            Assert.Equal(Defaults.MaximumItems, constraints.GetProperty("maximumItems").GetInt32());
            Assert.Equal(Defaults.FontSizePxMinimum, constraints.GetProperty("fontSizePxMinimum").GetInt32());
            Assert.Equal(Defaults.FontSizePxMaximum, constraints.GetProperty("fontSizePxMaximum").GetInt32());
            Assert.Equal(Defaults.MarginPxMinimum, constraints.GetProperty("marginPxMinimum").GetInt32());
            Assert.Equal(Defaults.MarginPxMaximum, constraints.GetProperty("marginPxMaximum").GetInt32());
            Assert.Equal(Defaults.RegionRatioMinimum, constraints.GetProperty("regionRatioMinimum").GetDouble());
            Assert.Equal(Defaults.RegionRatioMaximum, constraints.GetProperty("regionRatioMaximum").GetDouble());
            Assert.Equal(Defaults.RegionRatioStep, constraints.GetProperty("regionRatioStep").GetDouble());
        }
        var missing = await Invoke(app, "/api/performance-overlay/software/{softwareId}", "GET", "missing");
        using (var document = JsonDocument.Parse(missing.Body))
        {
            Assert.False(document.RootElement.GetProperty("stored").GetBoolean());
            Assert.False(document.RootElement.GetProperty("settings").GetProperty("enabled").GetBoolean());
            Assert.Equal(Defaults.FontSizePx, document.RootElement.GetProperty("settings").GetProperty("fontSizePx").GetInt32());
        }
        var toggle = await Invoke(app, "/api/performance-overlay/toggle", "POST", json: """{"softwareId":"game"}""");
        Assert.Equal(202, toggle.Status);
        using (var document = JsonDocument.Parse(toggle.Body))
        {
            Assert.True(document.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal("savedNotApplied", document.RootElement.GetProperty("runtimeApplicationDisposition").GetString());
        }
        Assert.Equal(24, store.Settings.PerformanceOverlay!.Software["Game"].FontSizePx);
        Assert.True(store.Settings.PerformanceOverlay!.Software["other"].Enabled);
    }

    [Fact]
    public async Task ReadOnlyProfileDoesNotMapToggle()
    {
        await using var app = Endpoints(new Store(), StartupCapabilitySet.NormalReadOnly);
        Assert.DoesNotContain(((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>(),
            static endpoint => endpoint.RoutePattern.RawText == "/api/performance-overlay/toggle");
    }

    [Theory]
    [InlineData("{\"softwareId\":\" \"}", 400)]
    [InlineData("{\"softwareId\":12}", 400)]
    [InlineData("{\"softwareId\":\"game\",\"enabled\":\"yes\"}", 400)]
    [InlineData("{", 400)]
    [InlineData("{\"softwareId\":\"game\"}", 409)]
    public async Task ToggleEndpointRejectsMalformedBodiesAndReturnsWriteReasons(string json, int status)
    {
        var store = new Store { RejectWith = "settings-revision-conflict" };
        await using var app = Endpoints(store, StartupCapabilitySet.Full);
        var result = await Invoke(app, "/api/performance-overlay/toggle", "POST", json: json);
        Assert.Equal(status, result.Status);
        Assert.Empty(store.Settings.PerformanceOverlay!.Software);
        if (status == 409)
        {
            using var document = JsonDocument.Parse(result.Body);
            Assert.Equal("settings-revision-conflict", document.RootElement.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task ForegroundGetReportsMissingPublicationWithoutAResourceSamplingService()
    {
        var store = new Store();
        await using var app = Endpoints(store, StartupCapabilitySet.Full);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/api/performance-overlay/foreground");
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString("?processId=42&processStartKey=101");
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        Assert.Equal(409, context.Response.StatusCode);
        using var document = JsonDocument.Parse(body.ToArray());
        Assert.Equal(PerformanceOverlayRejections.ProcessInventoryUnavailable, document.RootElement.GetProperty("reason").GetString());
    }

    private static WebApplication Endpoints(Store store, StartupCapabilitySet capabilities,
        PublishedSource? source = null, IResourceBreakdownPushSource? push = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.ConfigureHttpJsonOptions(options => ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton<IAppSettingsStore>(store);
        builder.Services.AddSingleton<IRuntimeSpecializationCoordinator>(store);
        builder.Services.AddSingleton<IComponentManager>(_ => null!);
        builder.Services.AddSingleton<HardwareMetricCatalogState>(_ => null!);
        builder.Services.AddSingleton<ISchedulingProcessFactObservationSource>(source ?? new PublishedSource(null));
        if (push is not null) builder.Services.AddSingleton(push);
        var app = builder.Build();
        app.MapPerformanceOverlayEndpoints(capabilities);
        return app;
    }

    private static async Task<(int Status, byte[] Body)> Invoke(WebApplication app, string pattern, string method,
        string softwareId = "", string? json = null, string query = "")
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == pattern && item.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = method;
        context.Request.RouteValues["softwareId"] = softwareId;
        context.Request.QueryString = new QueryString(query);
        await using var request = new MemoryStream(json is null ? [] : Encoding.UTF8.GetBytes(json));
        if (json is not null)
        {
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = request.Length;
            context.Request.Body = request;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
        }
        await using var response = new MemoryStream();
        context.Response.Body = response;
        await endpoint.RequestDelegate!(context);
        return (context.Response.StatusCode, response.ToArray());
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }

    private sealed class FailIfSampled : IResourceBreakdownPushSource
    {
        public IAsyncEnumerable<ResourceBreakdownSnapshot> SubscribeAsync(string id, ResourceBreakdownSampleRequest request,
            TimeSpan interval, CancellationToken token) => throw new InvalidOperationException("Current attribution must not trigger sampling.");
    }

    private sealed class PublishedSource(SchedulingProcessFactSnapshot? snapshot) : ISchedulingProcessFactObservationSource
    {
        public SchedulingProcessFactSnapshot? Current { get; set; } = snapshot;
        public IDisposable AcquireSubscription(string subscriptionId, SchedulingProcessMetricMask metricMask, TimeSpan refreshInterval)
            => throw new InvalidOperationException("Foreground inspection must not acquire sampling demand.");
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request)
        {
            Assert.Equal(SchedulingProcessMetricMask.None, request.RequestedMetricMask);
            return Current;
        }
    }

    private static Task<(PerformanceOverlayToggleReceipt? Receipt, string? Rejection)> Toggle(
        Store store, PerformanceOverlayToggleRequest? request, ForegroundSoftwareResolution? foreground)
        => PerformanceOverlayToggle.ToggleAsync(request,
            _ => Task.FromResult(foreground ?? throw new InvalidOperationException("An explicit id must not resolve the foreground.")),
            _ => Task.FromResult(store.Result), store.PatchAsync, CancellationToken.None);

    /// <summary>The settings write path as the toggle sees it: merge patch, normalize, validate, commit.</summary>
    private sealed class Store : IAppSettingsStore, IRuntimeSpecializationCoordinator
    {
        private readonly object gate = new();
        public Store(Dictionary<string, AppPerformanceOverlaySoftwareSettings>? software = null)
        {
            Settings = AppSettingsNormalizer.Normalize(AppSettingsDefaults.Create() with { PerformanceOverlay = new(software ?? new Dictionary<string, AppPerformanceOverlaySoftwareSettings>()) });
            Result = Wrap(Settings, 0);
        }

        public AppSettings Settings { get; private set; }
        public AppSettingsUpdateResult Result { get; private set; }
        public int Patches { get; private set; }
        public string? RejectWith { get; init; }
        public string CommitDisposition { get; init; } = AppSettingsRuntimeApplicationDisposition.CommittedAndApplied;

        public Task<AppSettingsUpdateResult> PatchAsync(AppSettingsPatchRequest request, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (Result.Revision != request.ExpectedRevision)
                    return Task.FromResult(Result with { RuntimeApplicationDisposition = "revisionConflict", RuntimeFailureCode = "settings-revision-conflict" });
                Patches++;
                var next = AppSettingsNormalizer.Normalize(AppSettingsPatchApplier.Apply(Settings, request.Changes));
                var reason = RejectWith ?? Defaults.Validate(next.PerformanceOverlay);
                if (reason is not null)
                    return Task.FromResult(Result with { RuntimeApplicationDisposition = "rejectedBeforeCommit", RuntimeFailureCode = reason });
                Settings = next;
                Result = Wrap(next, Patches) with { RuntimeApplicationDisposition = CommitDisposition };
                return Task.FromResult(Result);
            }
        }

        private static AppSettingsUpdateResult Wrap(AppSettings settings, int revision)
            => new(settings, DateTimeOffset.UnixEpoch, "settings",
                new(AppSettingsSourceKind.Persisted, settings.Version, "input", $"revision-{revision}", false));

        public Task<AppSettingsUpdateResult> LoadAsync(CancellationToken token) => Task.FromResult(Result);
        public Task<AppSettingsUpdateResult> LoadReadOnlyAsync(CancellationToken token) => Task.FromResult(Result);
        public Task<AppSettingsUpdateResult> SaveAsync(AppSettings settings, CancellationToken token) => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> SaveValidatedAsync(AppSettings settings, Func<AppSettingsUpdateResult, CancellationToken, Task> validate, CancellationToken token) => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> ApplyAppSettingsPatchAsync(AppSettingsPatchRequest patch, string reason, CancellationToken token) => PatchAsync(patch, token);
        public Task<AppSettingsUpdateResult> ApplyAppSettingsAsync(AppSettings settings, string reason, CancellationToken token) => throw new NotSupportedException();
        public Task<CpuBaselineRatioUpdateResult> ApplyCpuBaselineRatioAsync(double? ratio, CancellationToken token) => throw new NotSupportedException();
        public Task<RuntimePlanPublicationResult> RebuildAsync(string reason, CancellationToken token) => throw new NotSupportedException();
    }
}
