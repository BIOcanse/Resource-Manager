using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Domain.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.Software;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class SoftwareRegistryViewTests
{
    [Fact]
    public void SoftwareContractsContainFactsWithoutPresentationFields()
    {
        var fields = typeof(SoftwareRecord).GetProperties().Select(property => property.Name).ToHashSet();
        foreach (var removed in new[] { "Sources", "Message", "MessageCode", "DisplayKind" }) Assert.DoesNotContain(removed, fields);
        var capabilities = typeof(SoftwareOperationCapabilities).GetProperties().Select(property => property.Name).ToHashSet();
        Assert.Equal(new[] { "CanUninstall", "UninstallConstraint", "UninstallKind" }, capabilities.Order().ToArray());
        Assert.Contains("SourceFacts", fields);
        Assert.Contains("RegistrationFacts", fields);
    }

    [Fact]
    public async Task RefreshSoftwareAsync_PublishesNativeInventoryChangesOnceAndNotEqualReads()
    {
        var entries = new List<InstalledSoftwareEntry>
        {
            new("windows-installed:one", "One", "1.0", "Vendor", @"C:\Apps\One", [@"C:\Apps\One"], null, "registry:one")
        };
        var revisions = new ManagementInventoryRevisionOwner();
        var view = new SoftwareRegistryView(
            new StaticAdapterSoftwareRegistry([]),
            new InMemoryControlledSoftwareRegistry(),
            new EmptyDependencyManager(),
            new EmptyManualSoftwareRegistry(),
            new StaticSoftwarePolicyProfileProvider(new SoftwarePolicyProfile(
                new Dictionary<string, SoftwarePolicyGroup>(),
                new Dictionary<string, SoftwarePolicyAssignment>())),
            new WindowsInstalledSoftwareInventory(
                _ => entries,
                onSoftwareContentChanged: revisions.ObserveSoftwareInventoryContentChanged),
            SoftwareIdentityCatalogTestData.Empty,
            new StaticPortableSoftwareRegistry([]),
            revisions);

        _ = await view.GetSoftwareAsync(CancellationToken.None);
        var initial = revisions.Current;
        _ = await view.RefreshSoftwareAsync(CancellationToken.None);

        Assert.Equal(initial, revisions.Current);

        entries.Add(new InstalledSoftwareEntry(
            "windows-installed:two", "Two", "2.0", "Vendor", @"C:\Apps\Two", [@"C:\Apps\Two"], null, "registry:two"));
        _ = await view.RefreshSoftwareAsync(CancellationToken.None);

        Assert.Equal("1", revisions.Current.SoftwareRevision);
        Assert.Equal("0", revisions.Current.ComponentRevision);
        Assert.Equal("0", revisions.Current.BrowserRuntimeRevision);
    }

    [Fact]
    public async Task UserInstalledListClassificationAndScoreOverrideReachTheOwnersProcess()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        const string id = "windows-installed:users-s-1-5-21-1-2-3-1001-editor";
        const string root = @"C:\Users\Owner\AppData\Local\Editor";
        var now = DateTimeOffset.UtcNow;
        var view = CreateRuntimeReminderView(
            [new("manual:old", "Editor", SoftwareKinds.HighPerformance, [root], id, now, now)],
            [new(id, "Editor", null, null, root, [root], null, $@"Users\{sid}\Uninstall\Editor")]);
        var listed = Assert.Single(await view.GetSoftwareAsync(sid, default), record => record.Id == id);
        using var provider = RuntimeProcessAttributionCatalogProviderTests.CreateProvider(view);
        var plan = new ResourceManager.App.Domain.RuntimeSpecialization.CompiledBaseScorePlan(
            new Dictionary<string, double> { [id] = 87 }, new Dictionary<string, double>());
        var capture = await ResourceManager.App.Application.ProcessAttribution.RuntimeProcessAttributionCapture.CaptureAsync(provider,
            [new(53000, null, "editor", root + @"\editor.exe", false, StartKey: 10, UserSid: sid)], plan, default);
        var attributed = capture.Batch.SoftwareByProcessId[53000];
        Assert.Equal(id, listed.Id);
        Assert.Equal(listed.Id, attributed.Id);
        Assert.Equal(listed.Kind, attributed.Kind);
        Assert.Equal(SoftwareKinds.HighPerformance, attributed.Kind);
        Assert.Equal(87, plan.ResolveBaseScore(attributed.Id, attributed.Kind, null));
    }

    [Fact]
    public async Task ClassifyingLauncherDoesNotReplaceItsInstalledChildGame()
    {
        var now = DateTimeOffset.UtcNow;
        var view = CreateRuntimeReminderView([
            new("manual:old", "Steam", SoftwareKinds.Other, [@"D:\Steam"], "installed:steam", now, now)],
            [new("installed:steam", "Steam", null, null, @"D:\Steam", [@"D:\Steam"], null, "steam"),
             new("installed:game", "Game", null, null, @"D:\Steam\Games\Game", [@"D:\Steam\Games\Game"], null, "game")]);
        var records = await view.GetSoftwareAsync(default);
        Assert.Contains(records, record => record.Id == "installed:game");
        Assert.Contains(records, record => record.Id == "installed:steam" && record.HasExplicitKind);
    }

    [Fact]
    public async Task PortableDefaultsComeFromTheCurrentCatalogWhilePathsRemainRegistered()
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "portable", "Current Name", SoftwareKinds.Game, "test", ExecutableNames: ["CurrentPortableGame.exe"]));
        var portable = new StaticPortableSoftwareRegistry([new("catalog:portable", "portable", "Stale Name", SoftwareKinds.Other,
            [@"D:\Portable\game.exe"], [@"D:\Portable"], DateTimeOffset.UtcNow, IdentityConfirmed: true, RequiresRootPathConfirmation: false)]);
        var view = new SoftwareRegistryView(new StaticAdapterSoftwareRegistry([]), new InMemoryControlledSoftwareRegistry(),
            new EmptyDependencyManager(), new EmptyManualSoftwareRegistry(),
            new StaticSoftwarePolicyProfileProvider(new(new Dictionary<string, SoftwarePolicyGroup>(), new Dictionary<string, SoftwarePolicyAssignment>())),
            new StaticInstalledSoftwareInventory([]), catalog, portable);
        var record = Assert.Single(await view.GetSoftwareAsync(default), item => item.Id == "catalog:portable");
        Assert.Equal("Current Name", record.Name);
        Assert.Equal(SoftwareKinds.Game, record.Kind);
        Assert.Equal(@"D:\Portable", Assert.Single(record.RootPaths));
    }

    internal static SoftwareRegistryView CreateRuntimeReminderView(
        IReadOnlyList<ManualSoftwareRecord>? manual = null,
        IReadOnlyList<InstalledSoftwareEntry>? installed = null,
        IManualSoftwareRegistry? registry = null)
        => new(new StaticAdapterSoftwareRegistry([]), new InMemoryControlledSoftwareRegistry(),
            new EmptyDependencyManager(), registry ?? new StaticManualSoftwareRegistry(manual ?? []),
            new StaticSoftwarePolicyProfileProvider(new SoftwarePolicyProfile(
                new Dictionary<string, SoftwarePolicyGroup>(), new Dictionary<string, SoftwarePolicyAssignment>())),
            new StaticInstalledSoftwareInventory(installed ?? []), SoftwareIdentityCatalogTestData.Empty,
            new StaticPortableSoftwareRegistry([]));

    [Fact]
    public async Task GetSoftwareAsync_ForwardsAuthenticatedCallerSidToWindowsInventory()
    {
        const string callerSid = "S-1-5-21-1-2-3-1001";
        var inventory = new StaticInstalledSoftwareInventory([]);
        var dependencyManager = new EmptyDependencyManager();
        var view = new SoftwareRegistryView(
            new StaticAdapterSoftwareRegistry([]),
            new InMemoryControlledSoftwareRegistry(),
            dependencyManager,
            new EmptyManualSoftwareRegistry(),
            new StaticSoftwarePolicyProfileProvider(new SoftwarePolicyProfile(
                new Dictionary<string, SoftwarePolicyGroup>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SoftwarePolicyAssignment>(StringComparer.OrdinalIgnoreCase))),
            inventory,
            SoftwareIdentityCatalogTestData.Empty,
            new StaticPortableSoftwareRegistry([]));

        _ = await view.GetSoftwareAsync(callerSid, CancellationToken.None);

        Assert.Equal(callerSid, inventory.RequestedUserSid);
        Assert.Equal(callerSid, dependencyManager.RequestedWindowsSid);
    }

    [Fact]
    public async Task GetSoftwareAsync_ClassifiesExactCatalogIdentityBeforePolicyOverrides()
    {
        const string softwareId = "windows-installed:steam-app-489830";
        var profile = new SoftwarePolicyProfile(
            new Dictionary<string, SoftwarePolicyGroup>(StringComparer.OrdinalIgnoreCase)
            {
                ["targets.high-performance"] = new(
                    "targets.high-performance",
                    "高性能目标",
                    "high_performance",
                    ["HighPerformanceCandidate"])
            },
            new Dictionary<string, SoftwarePolicyAssignment>(StringComparer.OrdinalIgnoreCase)
            {
                [softwareId] = new(
                    softwareId,
                    "The Elder Scrolls V: Skyrim Special Edition",
                    "targets.high-performance",
                    [@"F:\Portable\Skyrim"])
            });
        var identityCatalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "game-skyrim-special-edition",
            "The Elder Scrolls V: Skyrim Special Edition",
            SoftwareKinds.Game,
            "test",
            SteamAppIds: [489830]));
        var view = new SoftwareRegistryView(
            new StaticAdapterSoftwareRegistry([]),
            new InMemoryControlledSoftwareRegistry(),
            new EmptyDependencyManager(),
            new EmptyManualSoftwareRegistry(),
            new StaticSoftwarePolicyProfileProvider(profile),
            new StaticInstalledSoftwareInventory([
                new InstalledSoftwareEntry(
                    softwareId,
                    "The Elder Scrolls V: Skyrim Special Edition",
                    null,
                    "Bethesda",
                    @"F:\Portable\Skyrim",
                    [@"F:\Portable\Skyrim"],
                    null,
                    @"HKLM\...\steam-app-489830")
            ]),
            identityCatalog,
            new StaticPortableSoftwareRegistry([]));

        var record = Assert.Single(await view.GetSoftwareAsync(CancellationToken.None), item => item.Id == softwareId);

        Assert.Equal(SoftwareKinds.HighPerformance, record.Kind);
        Assert.True(record.HasExplicitKind);
        Assert.Equal("game-skyrim-special-edition", record.SoftwareIdentityId);
        Assert.Contains(record.SourceFacts, static source => source.Kind == SoftwareSourceKinds.IdentityCatalog && source.CatalogVersion == "1.0.0");
    }

    [Fact]
    public async Task GetSoftwareAsync_ManualOverridePreservesExactSourceIdentity()
    {
        const string sourceSoftwareId = "windows-installed:steam-app-489830";
        var now = DateTimeOffset.UtcNow;
        var manualRecord = new ManualSoftwareRecord(
            "manual:skyrim",
            "Skyrim 自定义分类",
            SoftwareKinds.Game,
            [@"F:\Portable\Skyrim"],
            sourceSoftwareId,
            now,
            now);
        var identityCatalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "game-skyrim-special-edition",
            "The Elder Scrolls V: Skyrim Special Edition",
            SoftwareKinds.Game,
            "test",
            SteamAppIds: [489830]));
        var view = new SoftwareRegistryView(
            new StaticAdapterSoftwareRegistry([]),
            new InMemoryControlledSoftwareRegistry(),
            new EmptyDependencyManager(),
            new StaticManualSoftwareRegistry([manualRecord]),
            new StaticSoftwarePolicyProfileProvider(new SoftwarePolicyProfile(
                new Dictionary<string, SoftwarePolicyGroup>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SoftwarePolicyAssignment>(StringComparer.OrdinalIgnoreCase))),
            new StaticInstalledSoftwareInventory([
                new InstalledSoftwareEntry(
                    sourceSoftwareId,
                    "The Elder Scrolls V: Skyrim Special Edition",
                    null,
                    "Bethesda",
                    @"F:\Portable\Skyrim",
                    [@"F:\Portable\Skyrim"],
                    null,
                    @"HKLM\...\steam-app-489830")
            ]),
            identityCatalog,
            new StaticPortableSoftwareRegistry([]));

        var record = Assert.Single(
            await view.GetSoftwareAsync(CancellationToken.None),
            item => item.Id == sourceSoftwareId);

        Assert.Equal(sourceSoftwareId, record.Id);
        Assert.True(record.HasExplicitKind);
        Assert.Equal("game-skyrim-special-edition", record.SoftwareIdentityId);
    }

    [Fact]
    public async Task GetSoftwareAsync_ProjectsGameGroupAndDropsBroadSteamRoot()
    {
        var chroniconId = "windows-installed:localmachine-registry64-software-microsoft-windows-currentversion-uninstall-steam-app-375480";
        var view = new SoftwareRegistryView(
            new StaticAdapterSoftwareRegistry([]),
            new InMemoryControlledSoftwareRegistry(),
            new EmptyDependencyManager(),
            new EmptyManualSoftwareRegistry(),
            new StaticSoftwarePolicyProfileProvider(new SoftwarePolicyProfile(
                new Dictionary<string, SoftwarePolicyGroup>(StringComparer.OrdinalIgnoreCase)
                {
                    ["targets.games"] = new(
                        "targets.games",
                        "游戏目标",
                        "TargetCandidate",
                        ["AllowA1", "AllowA2"])
                },
                new Dictionary<string, SoftwarePolicyAssignment>(StringComparer.OrdinalIgnoreCase)
                {
                    [chroniconId] = new(
                        chroniconId,
                        "Chronicon - 英雄旧忆",
                        "targets.games",
                        [@"D:\Games\Steam\steamapps\common\Chronicon", @"D:\Games\Steam"])
                })),
            new StaticInstalledSoftwareInventory([
                new InstalledSoftwareEntry(
                    chroniconId,
                    "Chronicon - 英雄旧忆",
                    null,
                    "Steam",
                    @"D:\Games\Steam",
                    [@"D:\Games\Steam\steamapps\common\Chronicon", @"D:\Games\Steam"],
                    null,
                    @"HKLM\...\steam-app-375480"),
                new InstalledSoftwareEntry(
                    "windows-installed:steam",
                    "Steam",
                    null,
                    "Valve",
                    @"D:\Games\Steam",
                    [@"D:\Games\Steam"],
                    null,
                    @"HKLM\...\Steam")
            ]),
            SoftwareIdentityCatalogTestData.Empty,
            new StaticPortableSoftwareRegistry([]));

        var records = await view.GetSoftwareAsync(CancellationToken.None);

        var chronicon = Assert.Single(records, record => record.Id == chroniconId);
        Assert.Equal(SoftwareKinds.Game, chronicon.Kind);
        Assert.True(chronicon.HasExplicitKind);
        Assert.Contains(chronicon.SourceFacts, source => source.Kind == SoftwareSourceKinds.Policy);
        Assert.Equal([@"D:\Games\Steam\steamapps\common\Chronicon"], chronicon.RootPaths);

        var steam = Assert.Single(records, record => record.Id == "windows-installed:steam");
        Assert.Equal(SoftwareKinds.Other, steam.Kind);
        Assert.False(steam.HasExplicitKind);
    }

    [Fact]
    public async Task GetSoftwareAsync_UsesPersistentAdapterRegistrationAsAdaptedSoftware()
    {
        var adapter = CreateAdapterRegistration("adapter:word-memory", "背单词", [@"D:\Apps\WordMemory"]);
        var view = new SoftwareRegistryView(
            new StaticAdapterSoftwareRegistry([adapter]),
            new InMemoryControlledSoftwareRegistry(),
            new EmptyDependencyManager(),
            new EmptyManualSoftwareRegistry(),
            new StaticSoftwarePolicyProfileProvider(new SoftwarePolicyProfile(
                new Dictionary<string, SoftwarePolicyGroup>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SoftwarePolicyAssignment>(StringComparer.OrdinalIgnoreCase))),
            new StaticInstalledSoftwareInventory([
                new InstalledSoftwareEntry(
                    "windows-installed:word-memory",
                    "背单词",
                    null,
                    "Local",
                    @"D:\Apps\WordMemory",
                    [@"D:\Apps\WordMemory"],
                    null,
                    @"HKCU\...\WordMemory")
            ]),
            SoftwareIdentityCatalogTestData.Empty,
            new StaticPortableSoftwareRegistry([]));

        var records = await view.GetSoftwareAsync(CancellationToken.None);

        var adapted = Assert.Single(records, record => record.Id == "adapter:word-memory");
        Assert.Equal(SoftwareKinds.Adapted, adapted.Kind);
        Assert.Equal(SoftwareSourceKinds.Adapter, adapted.RegistrationFacts?.Kind);
        Assert.DoesNotContain(records, record => record.Id == "windows-installed:word-memory");
    }

    [Fact]
    public async Task GetSoftwareAsync_ResourceManagerSelfExposesBuiltInSchedulingAndSelfManagement()
    {
        var view = new SoftwareRegistryView(
            new StaticAdapterSoftwareRegistry([]),
            new InMemoryControlledSoftwareRegistry(),
            new EmptyDependencyManager(),
            new EmptyManualSoftwareRegistry(),
            new StaticSoftwarePolicyProfileProvider(new SoftwarePolicyProfile(
                new Dictionary<string, SoftwarePolicyGroup>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SoftwarePolicyAssignment>(StringComparer.OrdinalIgnoreCase))),
            new StaticInstalledSoftwareInventory([]),
            SoftwareIdentityCatalogTestData.Empty,
            new StaticPortableSoftwareRegistry([]));

        var records = await view.GetSoftwareAsync(CancellationToken.None);

        var self = Assert.Single(records, record => record.Id == RuntimeAttributionIds.ResourceManagerSelf);
        Assert.Equal(SoftwareKinds.Adapted, self.Kind);
        Assert.Contains(self.SourceFacts, source => source.Kind == SoftwareSourceKinds.Self);
        Assert.Equal(SoftwareSourceKinds.Self, self.RegistrationFacts?.Kind);
        Assert.Equal(SoftwareUninstallConstraints.Self, self.Operations.UninstallConstraint);
        Assert.NotEmpty(self.RootPaths);
    }

    [Fact]
    public async Task RefreshSoftwareAsync_ValidatesPortableRegistrationsOnlyForExplicitRefresh()
    {
        var portable = new StaticPortableSoftwareRegistry([
            new PortableSoftwareRegistration(
                "catalog:game-skyrim-special-edition",
                "game-skyrim-special-edition",
                "The Elder Scrolls V: Skyrim Special Edition",
                SoftwareKinds.Game,
                [@"F:\Portable\Skyrim\SkyrimSE.exe"],
                [@"F:\Portable\Skyrim"],
                DateTimeOffset.UtcNow,
                SuggestedRootPaths: [@"F:\Portable\Skyrim"],
                IdentityConfirmed: true,
                RequiresRootPathConfirmation: false)
        ]);
        var view = new SoftwareRegistryView(
            new StaticAdapterSoftwareRegistry([]),
            new InMemoryControlledSoftwareRegistry(),
            new EmptyDependencyManager(),
            new EmptyManualSoftwareRegistry(),
            new StaticSoftwarePolicyProfileProvider(new SoftwarePolicyProfile(
                new Dictionary<string, SoftwarePolicyGroup>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SoftwarePolicyAssignment>(StringComparer.OrdinalIgnoreCase))),
            new StaticInstalledSoftwareInventory([]),
            SoftwareIdentityCatalogTestData.Empty,
            portable);

        var cached = await view.GetSoftwareAsync(CancellationToken.None);
        Assert.Equal(0, portable.RefreshCount);
        var refreshed = await view.RefreshSoftwareAsync(CancellationToken.None);

        Assert.Equal(1, portable.RefreshCount);
        Assert.Contains(cached, static item => item.Id == "catalog:game-skyrim-special-edition");
        var record = Assert.Single(refreshed, static item => item.Id == "catalog:game-skyrim-special-edition");
        Assert.Equal(SoftwareKinds.Other, record.Kind);
        Assert.Equal("portable", record.State);
        Assert.Equal("game-skyrim-special-edition", record.SoftwareIdentityId);
        Assert.False(record.Operations.CanUninstall);
    }

    private sealed class StaticInstalledSoftwareInventory(IReadOnlyList<InstalledSoftwareEntry> entries) : IInstalledSoftwareInventory
    {
        public string? RequestedUserSid { get; private set; }

        public Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(entries);
        }

        public Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(
            string? windowsSid,
            CancellationToken cancellationToken)
        {
            RequestedUserSid = windowsSid;
            return GetInstalledSoftwareAsync(cancellationToken);
        }
    }

    private sealed class StaticPortableSoftwareRegistry(IReadOnlyList<PortableSoftwareRegistration> registrations)
        : IPortableSoftwareRegistry
    {
        public int RefreshCount { get; private set; }

        public void Observe(PortableSoftwareObservation observation)
        {
        }

        public IReadOnlyList<PortableSoftwareRegistration> GetSnapshot()
        {
            return registrations;
        }

        public Task<IReadOnlyList<PortableSoftwareRegistration>> RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.FromResult(registrations);
        }

        public Task<PortableSoftwareRootConfirmationResult> ConfirmRootPathAsync(
            PortableSoftwareRootConfirmationRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new PortableSoftwareRootConfirmationResult(
                request.SoftwareId,
                request.RootPath,
                1,
                false,
                "Confirmed",
                "confirmed"));
        }
    }

    private sealed class StaticAdapterSoftwareRegistry(IReadOnlyList<AdapterSoftwareRegistration> registrations) : IAdapterSoftwareRegistry
    {
        public Task<IReadOnlyList<AdapterSoftwareRegistration>> GetAllAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(registrations);
        }

        public Task<AdapterRegistrationResult> RegisterAsync(
            AdapterSoftwareRegistrationRequest request,
            AdapterResourceMarkerProbeResult markerProbe,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> RemoveAsync(string id, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private static AdapterSoftwareRegistration CreateAdapterRegistration(
        string id,
        string displayName,
        IReadOnlyList<string> roots)
    {
        return new AdapterSoftwareRegistration(
            id,
            AdapterRegistrationSchemaVersions.Current,
            $"{id}.adapter",
            $"{id}.app",
            displayName,
            null,
            roots,
            [],
            [],
            new AdapterResourceMarkerEndpoint(AdapterResourceMarkerTransports.LoopbackHttp, "http://127.0.0.1:9322/ledger"),
            new AdapterResourceMarkerProbeResult(AdapterResourceMarkerStates.Online, DateTimeOffset.Now, 200, "ok"),
            "registered",
            DateTimeOffset.Now,
            DateTimeOffset.Now);
    }

    private sealed class StaticSoftwarePolicyProfileProvider(SoftwarePolicyProfile profile) : ISoftwarePolicyProfileProvider
    {
        public Task<SoftwarePolicyProfile> GetProfileAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(profile);
        }
    }

    private sealed class EmptyManualSoftwareRegistry : IManualSoftwareRegistry
    {
        public Task<IReadOnlyList<ManualSoftwareRecord>> GetAllAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ManualSoftwareRecord>>([]);
        }

        public Task<ManualSoftwareRecord> AddOrUpdateAsync(ManualSoftwareRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> RemoveAsync(string id, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StaticManualSoftwareRegistry(IReadOnlyList<ManualSoftwareRecord> records)
        : IManualSoftwareRegistry
    {
        public Task<IReadOnlyList<ManualSoftwareRecord>> GetAllAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(records);
        }

        public Task<ManualSoftwareRecord> AddOrUpdateAsync(
            ManualSoftwareRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> RemoveAsync(string id, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class EmptyDependencyManager : IOptionalDependencyManager
    {
        public string? RequestedWindowsSid { get; private set; }

        public Task<IReadOnlyList<OptionalDependencyStatus>> GetStatusesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<OptionalDependencyStatus>>([]);
        }

        public Task<IReadOnlyList<OptionalDependencyStatus>> GetStatusesAsync(
            string? windowsSid,
            CancellationToken cancellationToken)
        {
            RequestedWindowsSid = windowsSid;
            return GetStatusesAsync(cancellationToken);
        }

        public Task<OptionalDependencyStatus?> GetStatusAsync(string id, CancellationToken cancellationToken)
        {
            return Task.FromResult<OptionalDependencyStatus?>(null);
        }

        public Task<DependencyVersionOptions> GetVersionOptionsAsync(string id, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<OptionalDependencyDownloadResult> DownloadAsync(
            string id,
            bool acknowledgeExternalTerms,
            string? versionChoice,
            CancellationToken cancellationToken,
            IProgress<DependencyDownloadProgress>? progress = null)
        {
            throw new NotSupportedException();
        }

        public Task<OptionalDependencyLaunchResult> LaunchInstallerAsync(
            string id,
            bool acknowledgeExternalTerms,
            string? versionChoice,
            CancellationToken cancellationToken,
            SoftwareOperationCaller? caller = null)
        {
            throw new NotSupportedException();
        }
    }
}
