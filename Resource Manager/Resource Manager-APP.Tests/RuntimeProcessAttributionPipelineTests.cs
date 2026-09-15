using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class RuntimeProcessAttributionPipelineTests
{
    [Theory]
    [InlineData(false, SoftwareKinds.Other)]
    [InlineData(true, SoftwareKinds.Other)]
    [InlineData(true, SoftwareKinds.HighPerformance)]
    public void Match_BundledIdentityUsesRegisteredPayload(bool confirmedRoot, string kind)
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "app-wps-office", "WPS Office", SoftwareKinds.Other, "test",
            ExecutableNames: ["wps.exe"]));
        var software = CreateSoftware("catalog:app-wps-office", "WPS Office",
            confirmedRoot ? [@"D:\Software\WPS Office"] : []) with
        {
            Kind = kind,
            DisplayKind = SoftwareDisplayKinds.Project(kind)
        };
        using var pipeline = CreatePipeline([software], identityCatalog: catalog);
        var result = pipeline.Match(new RuntimeProcessIdentity(
            46101, null, "wps", @"D:\Software\WPS Office\12.1\office6\wps.exe",
            false, ProductName: "WPS Office"));
        Assert.Equal(software.Id, result.Id);
        Assert.Equal(software.Name, result.Name);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(confirmedRoot ? ["d:/software/wps office"] : Array.Empty<string>(), result.RootPaths);
    }

    [Fact]
    public void Match_UsesBundledIdentityForPortableGame()
    {
        var identityCatalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "game-skyrim-special-edition",
            "The Elder Scrolls V: Skyrim Special Edition",
            SoftwareKinds.Game,
            "test",
            ExecutableNames: ["SkyrimSE.exe"]));
        var pipeline = CreatePipeline([], identityCatalog: identityCatalog);

        var attribution = pipeline.Match(new RuntimeProcessIdentity(
            46100,
            null,
            "SkyrimSE",
            @"C:\Games\Skyrim Special Edition\SkyrimSE.exe",
            false));

        Assert.NotNull(attribution);
        Assert.Equal("catalog:game-skyrim-special-edition", attribution.Id);
        Assert.Equal(SoftwareKinds.Game, attribution.Kind);
        Assert.Equal(95d, ResourceManager.App.Domain.Optimization.Scoring.OptimizationRuntimeScoringDefaults.BaseScoreForKind(attribution.Kind));
    }

    [Fact]
    public void Match_DoesNotPromoteWindowTitleAndContinuesToRuntimeProduct()
    {
        var pipeline = CreatePipeline([
            CreateSoftware("windows-installed:star-rail", "崩坏: 星穹铁道", [])
        ]);
        var process = new RuntimeProcessIdentity(
            100,
            null,
            "DocumentViewer",
            @"D:\Tools\DocumentViewer\DocumentViewer.exe",
            false,
            FileDescription: "Document Viewer",
            ProductName: "Document Viewer",
            CompanyName: "Example",
            WindowTitle: "崩坏: 星穹铁道");

        var attribution = pipeline.Match(process);

        Assert.Equal("runtime-product:example-document-viewer", attribution.Id);
    }

    [Fact]
    public void Match_PrefersLauncherRootWhenLauncherTitleContainsGameName()
    {
        var pipeline = CreatePipeline([
            CreateSoftware("windows-installed:star-rail", "崩坏: 星穹铁道", []),
            CreateSoftware("windows-installed:hoyoplay", "米哈游启动器", [@"D:\Games\HoYoPlay"])
        ]);
        var process = new RuntimeProcessIdentity(
            101,
            null,
            "HYPHelper",
            @"D:\Games\HoYoPlay\HYPHelper.exe",
            false,
            FileDescription: "HYPHelper",
            ProductName: "HoYoPlay",
            CompanyName: "miHoYo",
            WindowTitle: "崩坏: 星穹铁道");

        var attribution = pipeline.Match(process);

        Assert.NotNull(attribution);
        Assert.Equal("windows-installed:hoyoplay", attribution.Id);
    }

    [Fact]
    public void Match_AllowsNestedGameProcessToSeparateFromLauncherRoot()
    {
        var pipeline = CreatePipeline([
            CreateSoftware("windows-installed:hoyoplay", "米哈游启动器", [@"D:\Games\HoYoPlay"]),
            CreateSoftware("windows-installed:star-rail", "崩坏: 星穹铁道", [@"D:\Games\HoYoPlay\Games\Star Rail"])
        ]);
        var process = new RuntimeProcessIdentity(
            102,
            null,
            "StarRail",
            @"D:\Games\HoYoPlay\Games\Star Rail\StarRail.exe",
            false,
            FileDescription: "崩坏: 星穹铁道",
            ProductName: "崩坏: 星穹铁道",
            CompanyName: "miHoYo",
            WindowTitle: "崩坏: 星穹铁道");

        var attribution = pipeline.Match(process);

        Assert.NotNull(attribution);
        Assert.Equal("windows-installed:star-rail", attribution.Id);
    }

    [Fact]
    public void Match_ClassifiesLauncherManagedGameWhenOnlyLauncherIsInstalled()
    {
        var pipeline = CreatePipeline([
            CreateSoftware("windows-installed:hoyoplay", "米哈游启动器", [@"D:\Games\miHoYo Launcher"])
        ]);
        var process = new RuntimeProcessIdentity(
            108,
            null,
            "StarRail",
            @"D:\Games\miHoYo Launcher\Games\Star Rail Game\StarRail.exe",
            false,
            FileDescription: "崩坏: 星穹铁道",
            ProductName: "崩坏: 星穹铁道",
            CompanyName: "miHoYo",
            WindowTitle: "崩坏: 星穹铁道");

        var attribution = pipeline.Match(process);

        Assert.NotNull(attribution);
        Assert.StartsWith("runtime-game:", attribution.Id, StringComparison.Ordinal);
        Assert.Equal("star rail game", attribution.Name);
        Assert.Equal(SoftwareKinds.Game, attribution.Kind);
        Assert.Equal(SoftwareDisplayKinds.Game, attribution.DisplayKind);
        Assert.Equal(
            "d:/games/mihoyo launcher/games/star rail game",
            Assert.Single(attribution.RootPaths));
    }

    [Fact]
    public void Match_GroupsLauncherManagedGameHelpersByGameDirectory()
    {
        var pipeline = CreatePipeline([
            CreateSoftware("windows-installed:hoyoplay", "米哈游启动器", [@"D:\Games\miHoYo Launcher"])
        ]);
        var game = new RuntimeProcessIdentity(
            109,
            null,
            "StarRail",
            @"D:\Games\miHoYo Launcher\Games\Star Rail Game\StarRail.exe",
            false,
            ProductName: "崩坏: 星穹铁道");
        var helper = new RuntimeProcessIdentity(
            110,
            109,
            "UnityCrashHandler64",
            @"D:\Games\miHoYo Launcher\Games\Star Rail Game\UnityCrashHandler64.exe",
            false,
            ProductName: "Unity Crash Handler");

        var gameAttribution = pipeline.Match(game);
        var helperAttribution = pipeline.Match(helper);

        Assert.NotNull(gameAttribution);
        Assert.NotNull(helperAttribution);
        Assert.Equal(gameAttribution.Id, helperAttribution.Id);
        Assert.Equal(SoftwareKinds.Game, helperAttribution.Kind);
    }

    [Fact]
    public void Match_ClassifiesResourceManagerSelfAsAdaptedSoftware()
    {
        var pipeline = CreatePipeline([]);
        var process = new RuntimeProcessIdentity(
            103,
            null,
            "ResourceManager.NativeUi",
            @"C:\Apps\ResourceManager\Bin\ResourceManagerNativeUi\ResourceManager.NativeUi.exe",
            true,
            FileDescription: "ResourceManager.NativeUi",
            ProductName: "Resource Manager",
            CompanyName: null,
            WindowTitle: "资源管理器");

        var attribution = pipeline.Match(process);

        Assert.NotNull(attribution);
        Assert.Equal(RuntimeAttributionIds.ResourceManagerSelf, attribution.Id);
        Assert.Equal(SoftwareKinds.Adapted, attribution.Kind);
        Assert.Equal(SoftwareDisplayKinds.Adapted, attribution.DisplayKind);
    }

    [Fact]
    public void Match_ClassifiesPersistedAdapterRegistrationAsAdaptedSoftware()
    {
        var adapter = new AdapterSoftwareRegistration(
            "adapter:word-memory",
            AdapterRegistrationSchemaVersions.Current,
            "word-memory.adapter",
            "word-memory",
            "背单词",
            null,
            [@"D:\Apps\WordMemory"],
            [new AdapterProcessDeclaration("WordMemory.WebHost", null, null)],
            [],
            new AdapterResourceMarkerEndpoint(AdapterResourceMarkerTransports.LoopbackHttp, "http://127.0.0.1:9322/ledger"),
            new AdapterResourceMarkerProbeResult(AdapterResourceMarkerStates.Online, DateTimeOffset.Now, 200, "ok"),
            "registered",
            DateTimeOffset.Now,
            DateTimeOffset.Now);
        var pipeline = CreatePipeline([], [adapter]);
        var process = new RuntimeProcessIdentity(
            104,
            null,
            "WordMemory.WebHost",
            @"D:\Apps\WordMemory\WordMemory.WebHost.exe",
            false,
            FileDescription: "WordMemory.WebHost",
            ProductName: "背单词",
            CompanyName: null,
            WindowTitle: "背单词");

        var attribution = pipeline.Match(process);

        Assert.NotNull(attribution);
        Assert.Equal("adapter:word-memory", attribution.Id);
        Assert.Equal(SoftwareKinds.Adapted, attribution.Kind);
        Assert.Equal(SoftwareDisplayKinds.Adapted, attribution.DisplayKind);
    }

    [Fact]
    public void Match_ClassifiesUndeclaredAdapterRootProcessAsAdaptedForScoringOnly()
    {
        var adapter = new AdapterSoftwareRegistration(
            "adapter:word-memory",
            AdapterRegistrationSchemaVersions.Current,
            "word-memory.adapter",
            "word-memory",
            "背单词",
            null,
            [@"D:\Apps\WordMemory"],
            [new AdapterProcessDeclaration("WordMemory.WebHost", null, null)],
            [],
            new AdapterResourceMarkerEndpoint(AdapterResourceMarkerTransports.LoopbackHttp, "http://127.0.0.1:9322/ledger"),
            new AdapterResourceMarkerProbeResult(AdapterResourceMarkerStates.Online, DateTimeOffset.Now, 200, "ok"),
            "registered",
            DateTimeOffset.Now,
            DateTimeOffset.Now);
        var pipeline = CreatePipeline([], [adapter]);
        var process = new RuntimeProcessIdentity(
            105,
            null,
            "WordMemory.KeepAliveHelper",
            @"D:\Apps\WordMemory\WordMemory.KeepAliveHelper.exe",
            false,
            FileDescription: "WordMemory.KeepAliveHelper",
            ProductName: "背单词",
            CompanyName: null,
            WindowTitle: "");

        var attribution = pipeline.Match(process);

        Assert.NotNull(attribution);
        Assert.Equal("adapter:word-memory", attribution.Id);
        Assert.Equal(SoftwareKinds.Adapted, attribution.Kind);
    }

    [Fact]
    public void Match_NormalizesSoftwareAndProcessIdentityOnceWithoutChangingMatchSemantics()
    {
        var pipeline = CreatePipeline([
            CreateSoftware("windows-installed:foo-bar", "Foo Bar", [])
        ]);
        var process = new RuntimeProcessIdentity(
            107,
            null,
            "foo_bar",
            @"D:\Tools\foo_bar.exe",
            false,
            ProductName: "FOO-BAR");

        var attribution = pipeline.Match(process);

        Assert.NotNull(attribution);
        Assert.Equal("windows-installed:foo-bar", attribution.Id);
    }

    [Fact]
    public void Match_AllowsDistinctRuntimeKnownIdentitiesToShareDisplayName()
    {
        var pipeline = CreatePipeline([
            CreateSoftware("windows-installed:client-one", "Game Client", [@"D:\Games\One"]),
            CreateSoftware("windows-installed:client-two", "Game Client", [@"D:\Games\Two"])
        ]);

        var attribution = pipeline.Match(new RuntimeProcessIdentity(
            109,
            null,
            "client",
            @"D:\Games\Two\Client.exe",
            false));

        Assert.Equal("windows-installed:client-two", attribution.Id);
    }

    [Fact]
    public void Match_ContinuesAfterExplicitlyUnavailableSource()
    {
        var serviceAttribution = new RuntimeSoftwareAttribution(
            "windows-service:test",
            "Test Service",
            SoftwareKinds.WindowsService,
            "系统组件",
            []);
        var pipeline = CreatePipeline(
            [],
            packageResolver: new UnavailablePackageIdentityResolver(),
            serviceResolver: new MatchedServiceIdentityResolver(serviceAttribution));

        var attribution = pipeline.Match(new RuntimeProcessIdentity(
            108,
            null,
            "test-service",
            @"C:\Windows\System32\test-service.exe",
            false));

        Assert.Equal(serviceAttribution, attribution);
    }

    private static RuntimeProcessAttributionPipeline CreatePipeline(
        IReadOnlyList<SoftwareRecord> software,
        IReadOnlyList<AdapterSoftwareRegistration>? adapterRegistrations = null,
        ResourceManager.App.Application.SoftwareIdentity.ISoftwareIdentityCatalog? identityCatalog = null,
        IRuntimePackageIdentityResolver? packageResolver = null,
        IRuntimeServiceIdentityResolver? serviceResolver = null)
    {
        return new RuntimeProcessAttributionPipeline(
            software,
            adapterRegistrations ?? [],
            Array.Empty<ControlledSoftwareRegistration>(),
            packageResolver ?? new NullPackageIdentityResolver(),
            serviceResolver ?? new NullServiceIdentityResolver(),
            new NullRootIdentityResolver(),
            new NullSystemProcessClassifier(),
            identityCatalog ?? SoftwareIdentityCatalogTestData.Empty,
            SoftwareIdentityCatalogTestData.Owner);
    }

    private static SoftwareRecord CreateSoftware(string id, string name, IReadOnlyList<string> rootPaths)
    {
        return new SoftwareRecord(
            id,
            name,
            SoftwareKinds.Other,
            "其他软件",
            "installed",
            ["test"],
            rootPaths,
            "",
            new SoftwareOperationCapabilities(false, "None", "不可卸载", ""),
            null);
    }

    private sealed class NullPackageIdentityResolver : IRuntimePackageIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }

    private sealed class UnavailablePackageIdentityResolver : IRuntimePackageIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.Unavailable;
    }

    private sealed class NullServiceIdentityResolver : IRuntimeServiceIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }

    private sealed class MatchedServiceIdentityResolver(
        RuntimeSoftwareAttribution attribution) : IRuntimeServiceIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.Matched(attribution);
    }

    private sealed class NullRootIdentityResolver : IRuntimeRootIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }

    private sealed class NullSystemProcessClassifier : IRuntimeSystemProcessClassifier
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }
}
