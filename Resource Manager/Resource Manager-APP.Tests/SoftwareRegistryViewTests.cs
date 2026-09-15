using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Migration;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Migration;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Domain.SoftwareDiscovery;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class SoftwareRegistryViewTests
{
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
                    "HighPerformance",
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
            new EmptyMigrationManager(),
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
        Assert.Equal("game-skyrim-special-edition", record.SoftwareIdentityId);
        Assert.Contains(record.Sources, static source => source.StartsWith("软件身份目录 1.0.0", StringComparison.Ordinal));
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
            SoftwareDisplayKinds.Game,
            "manual",
            ["手动补录"],
            [@"F:\Portable\Skyrim"],
            "用户维护的分类。",
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
            new EmptyMigrationManager(),
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
            item => item.Id == manualRecord.Id);

        Assert.Equal(manualRecord.Id, record.Id);
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
            new EmptyMigrationManager(),
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
        Assert.Equal(SoftwareDisplayKinds.Game, chronicon.DisplayKind);
        Assert.Equal([@"D:\Games\Steam\steamapps\common\Chronicon"], chronicon.RootPaths);

        var steam = Assert.Single(records, record => record.Id == "windows-installed:steam");
        Assert.Equal(SoftwareKinds.Other, steam.Kind);
    }

    [Fact]
    public async Task GetSoftwareAsync_UsesPersistentAdapterRegistrationAsAdaptedSoftware()
    {
        var adapter = CreateAdapterRegistration("adapter:word-memory", "背单词", [@"D:\Apps\WordMemory"]);
        var view = new SoftwareRegistryView(
            new StaticAdapterSoftwareRegistry([adapter]),
            new InMemoryControlledSoftwareRegistry(),
            new EmptyDependencyManager(),
            new EmptyMigrationManager(),
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
        Assert.Equal(SoftwareDisplayKinds.Adapted, adapted.DisplayKind);
        Assert.DoesNotContain(records, record => record.Id == "windows-installed:word-memory");
    }

    [Fact]
    public async Task GetSoftwareAsync_ResourceManagerSelfExposesBuiltInSchedulingAndSelfManagement()
    {
        var view = new SoftwareRegistryView(
            new StaticAdapterSoftwareRegistry([]),
            new InMemoryControlledSoftwareRegistry(),
            new EmptyDependencyManager(),
            new EmptyMigrationManager(),
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
        Assert.Contains("本地资源自管", self.Sources);
        Assert.Contains("内置调度控制", self.Message);
        Assert.DoesNotContain("资源标记", self.Message);
        Assert.NotEmpty(self.RootPaths);
        Assert.DoesNotContain("/api/adapters/resource-manager/ledger", self.Message);
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
            new EmptyMigrationManager(),
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
        Assert.Equal(SoftwareKinds.Game, record.Kind);
        Assert.Equal("portable", record.State);
        Assert.Equal("game-skyrim-special-edition", record.SoftwareIdentityId);
        Assert.False(record.Operations.CanUninstall);
    }

    private sealed class StaticInstalledSoftwareInventory(IReadOnlyList<InstalledSoftwareEntry> entries) : IInstalledSoftwareInventory
    {
        public Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(entries);
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

    private sealed class EmptyMigrationManager : ISoftwareDataMigrationManager
    {
        public MigrationRoots GetRoots()
        {
            return new MigrationRoots(@"D:\UserData", @"D:\Misc", @"D:\Dependencies", @"D:\Misc\SoftwareRoots");
        }

        public Task<IReadOnlyList<SoftwareDataMigrationRecord>> GetRecordsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<SoftwareDataMigrationRecord>>([]);
        }

        public SoftwareDataMigrationPlan Preview(SoftwareDataMigrationRequest request)
        {
            throw new NotSupportedException();
        }

        public Task<SoftwareDataMigrationResult> ExecuteAsync(SoftwareDataMigrationRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<SoftwareDataRestoreResult> RestoreAsync(SoftwareDataRestoreRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class EmptyDependencyManager : IOptionalDependencyManager
    {
        public Task<IReadOnlyList<OptionalDependencyStatus>> GetStatusesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<OptionalDependencyStatus>>([]);
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
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
