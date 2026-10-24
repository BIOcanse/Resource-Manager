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
    [Fact]
    public void GameCategoryWinsOverHelperCatalogCategoryRegardlessOfEnumerationOrder()
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "high-performance-helper", "Tool Helper", SoftwareKinds.HighPerformance, "test", ExecutableNames: ["KnownToolHelper.exe"]));
        const string gamePath = @"D:\Games\One\gameclient.exe";
        using var pipeline = CreatePipeline([CreateSoftware("installed:one", "One", [@"D:\Games\One"])], identityCatalog: catalog,
            windowsGamePaths: new HashSet<string> { gamePath });
        var result = pipeline.MatchBatch([
            new(951, 950, "KnownToolHelper", @"D:\Games\One\KnownToolHelper.exe", false, StartKey: 20),
            new(950, null, "gameclient", gamePath, false, StartKey: 10)]);
        Assert.All(result.SoftwareByProcessId.Values, item => Assert.Equal(SoftwareKinds.Game, item.Kind));
    }

    [Fact]
    public void CatalogGameCategoryReachesInstalledHelpersAndPreservesExplicitScores()
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "game-known", "Known Game", SoftwareKinds.Game, "test", ExecutableNames: ["KnownGameClient.exe"]));
        using var pipeline = CreatePipeline([CreateSoftware("installed:known", "Unmapped Installed Label", [@"D:\Games\Known"])], identityCatalog: catalog);
        var batch = pipeline.MatchBatch([
            new(940, null, "KnownGameClient", @"D:\Games\Known\KnownGameClient.exe", false, StartKey: 10),
            new(941, 940, "CrashReporter", @"D:\Games\Known\CrashReporter.exe", false, StartKey: 20)]);
        var scores = new ResourceManager.App.Domain.RuntimeSpecialization.CompiledBaseScorePlan(
            new Dictionary<string, double> { ["installed:known"] = 88 },
            new Dictionary<string, double> { ["installed:known\nprocess:helper"] = 77 });
        Assert.All(batch.SoftwareByProcessId.Values, value =>
        {
            Assert.Equal("installed:known", value.Id);
            Assert.Equal(SoftwareKinds.Game, value.Kind);
            Assert.Equal(88, scores.ResolveBaseScore(value.Id, value.Kind, null));
        });
        var helper = batch.SoftwareByProcessId[941];
        Assert.Equal(77, scores.ResolveBaseScore(helper.Id, helper.Kind, "process:helper"));
    }

    [Theory]
    [InlineData("host.exe", true)]
    [InlineData("different.exe", false)]
    public void ChromiumHelpersHonorTheWebViewHostSwitch(string declaredHost, bool inherits)
    {
        using var pipeline = CreatePipeline([CreateSoftware("installed:host", "Host", [@"D:\Apps\Host"])]);
        var batch = pipeline.MatchBatch([
            new(942, null, "host", @"D:\Apps\Host\host.exe", false, StartKey: 10),
            new(943, 942, "msedgewebview2", @"C:\Runtime\msedgewebview2.exe", false, StartKey: 20,
                CommandLine: $"webview --webview-exe-name={declaredHost}"),
            new(944, 943, "msedgewebview2", @"C:\Runtime\msedgewebview2.exe", false, StartKey: 30,
                CommandLine: $"webview --type=gpu-process --webview-exe-name={declaredHost}")]);
        Assert.Equal(inherits, batch.SoftwareByProcessId[944].Id == "installed:host");
        Assert.Equal(inherits, batch.HelperOwnerByProcessId.ContainsKey(944));
    }

    [Fact]
    public void ChromiumRendererInheritsButAServiceKeepsItsOwnOwnership()
    {
        var service = new ChangingServiceResolver();
        using var pipeline = CreatePipeline([CreateSoftware("installed:host", "Host", [@"D:\Apps\Host"])], serviceResolver: service);
        RuntimeProcessIdentity[] processes = [
            new(945, null, "host", @"D:\Apps\Host\host.exe", false, StartKey: 10),
            new(946, 945, "chromium", @"C:\Runtime\chromium.exe", false, StartKey: 20, CommandLine: "chromium --type=renderer")];
        Assert.Equal("installed:host", pipeline.MatchBatch(processes).SoftwareByProcessId[946].Id);
        service.Current = RuntimeAttributionObservation.Matched(new("windows-service:fixture", "Fixture", SoftwareKinds.WindowsService, SoftwareKinds.WindowsService, []));
        Assert.Equal("windows-service:fixture", pipeline.MatchBatch(processes).SoftwareByProcessId[946].Id);
    }

    [Fact]
    public void ExactCatalogGameIdentityReplacesLauncherDerivedIdentityForAllGameMembers()
    {
        var catalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "game-known", "Known Game", SoftwareKinds.Game, "test", ExecutableNames: ["KnownGameClient.exe"]));
        using var pipeline = CreatePipeline([CreateSoftware("installed:launcher", "miHoYo Launcher", [@"D:\miHoYo Launcher"])], identityCatalog: catalog);
        var result = pipeline.MatchBatch([
            new(947, null, "KnownGameClient", @"D:\miHoYo Launcher\Games\Known\KnownGameClient.exe", false, StartKey: 10),
            new(948, 947, "helper", @"D:\miHoYo Launcher\Games\Known\helper.exe", false, StartKey: 20)]);
        Assert.All(result.SoftwareByProcessId.Values, value => Assert.Equal("catalog:game-known", value.Id));
    }

    [Fact]
    public void EmbeddedCodexRuntimeUsesTheSameApplicationRootAcrossUserProfiles()
    {
        using var pipeline = CreatePipeline([], rootResolver: new ResourceManager.App.Infrastructure.ProcessIdentity.WindowsRuntimeRootIdentityResolver());
        const string app = @"C:\Users\AnotherUser\AppData\Local\OpenAI\Codex";
        var main = pipeline.Match(new(930, null, "Codex", app + @"\bin\Codex.exe", false, ProductName: "Codex"));
        var helper = pipeline.Match(new(931, null, "node_repl", app + @"\runtimes\cua_node\2b841c3d44b123ec\bin\node_repl.exe", false, ProductName: "Node.js"));
        Assert.Equal(main.Id, helper.Id);
        Assert.Equal("Codex", helper.Name);
        Assert.Equal(app, Assert.Single(helper.RootPaths), ignoreCase: true);
    }

    [Fact]
    public void DefenderPlatformIsAWindowsComponentAndDdkMetadataIsNotAnIdentity()
    {
        using var pipeline = new RuntimeProcessAttributionPipeline(
            [CreateSoftware("installed:wrong-windows-root", "PowerShell", [@"C:\Windows"])], [], [], new NullPackageIdentityResolver(),
            new NullServiceIdentityResolver(), new ResourceManager.App.Infrastructure.ProcessIdentity.WindowsRuntimeRootIdentityResolver(),
            new ResourceManager.App.Infrastructure.ProcessIdentity.WindowsSystemProcessClassifier(), SoftwareIdentityCatalogTestData.Empty,
            SoftwareIdentityCatalogTestData.Owner);
        var defenderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows Defender", "Platform", "4.18.0", "DefenderSessionHelper.exe");
        var defender = pipeline.Match(new(932, null, "DefenderSessionHelper", defenderPath, false,
            ProductName: "Microsoft® Windows® Operating System"));
        Assert.Equal(RuntimeAttributionIds.WindowsComponent, defender.Id);
        var powershell = pipeline.Match(new(934, null, "powershell",
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", false));
        Assert.Equal(RuntimeAttributionIds.WindowsComponent, powershell.Id);
        var gameInput = pipeline.Match(new(933, null, "GameInputRedistService",
            @"C:\Program Files\Microsoft GameInput\x64\GameInputRedistService.exe", false,
            ProductName: "Windows (R) Win 7 DDK provider"));
        Assert.DoesNotContain("ddk", gameInput.Id, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Microsoft GameInput", gameInput.Name);
    }

    [Theory]
    [InlineData("rustc", @"D:\Rust\rustc.exe", "Rust")]
    [InlineData("build", @"D:\Source\.zig-cache\build.exe", "Microsoft Build of OpenJDK")]
    [InlineData("powershell", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "PowerShell 7-x64")]
    [InlineData("pwsh", @"D:\Portable\pwsh.exe", "PowerShell 7-x64")]
    public void InstalledDisplayNamesNeverClaimExecutablesOutsideTheirRoot(string name, string path, string installedName)
    {
        using var pipeline = CreatePipeline([CreateSoftware("installed:unrelated", installedName, [@"C:\Program Files\Unrelated"])]);
        Assert.NotEqual("installed:unrelated", pipeline.Match(new(920, null, name, path, false, ProductName: installedName)).Id);
    }

    [Fact]
    public void BatchProjectsWindowsGameCategoryToEveryProcessOfTheSoftware()
    {
        const string gamePath = @"D:\Games\NewGame\game.exe";
        using var pipeline = CreatePipeline([CreateSoftware("installed:game", "New Game", [@"D:\Games\NewGame"])],
            windowsGamePaths: new HashSet<string> { gamePath });
        var result = pipeline.MatchBatch([
            new(921, null, "game", gamePath, false, StartKey: 10),
            new(922, 921, "helper", @"D:\Games\NewGame\helper.exe", false, StartKey: 20)]);
        Assert.All(result.SoftwareByProcessId.Values, value =>
        {
            Assert.Equal("installed:game", value.Id);
            Assert.Equal(SoftwareKinds.Game, value.Kind);
            Assert.NotNull(value.CategorySource);
            Assert.Equal(95, ResourceManager.App.Domain.Optimization.Scoring.OptimizationRuntimeScoringDefaults.BaseScoreForKind(value.Kind));
        });
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(30, false)]
    [InlineData(0, false)]
    public void WebViewBelongsToItsVerifiedHostInstance(long hostStart, bool inherits)
    {
        using var pipeline = CreatePipeline([CreateSoftware("installed:host", "Host", [@"D:\Apps\Host"])]);
        var result = pipeline.MatchBatch([
            new(923, null, "host", @"D:\Apps\Host\host.exe", false, StartKey: hostStart),
            new(924, 923, "msedgewebview2", @"C:\Runtime\msedgewebview2.exe", false, StartKey: 20)]);
        Assert.Equal(inherits, result.SoftwareByProcessId[924].Id == "installed:host");
    }

    [Fact]
    public void ServiceChangesAreObservedWithinTheSameProcessLifetime()
    {
        var services = new ChangingServiceResolver();
        using var pipeline = CreatePipeline([], serviceResolver: services);
        var process = new RuntimeProcessIdentity(925, null, "svchost", @"C:\Windows\System32\svchost.exe", false, StartKey: 10);
        Assert.NotEqual("service:one", pipeline.MatchBatch([process]).SoftwareByProcessId[925].Id);
        services.Current = RuntimeAttributionObservation.Matched(new("service:one", "One", SoftwareKinds.WindowsService, SoftwareDisplayKinds.WindowsService, []));
        Assert.Equal("service:one", pipeline.MatchBatch([process]).SoftwareByProcessId[925].Id);
        services.Current = RuntimeAttributionObservation.NoMatch;
        Assert.NotEqual("service:one", pipeline.MatchBatch([process]).SoftwareByProcessId[925].Id);
    }

    private sealed class ChangingServiceResolver : IRuntimeServiceIdentityResolver
    {
        public RuntimeAttributionObservation Current { get; set; } = RuntimeAttributionObservation.NoMatch;
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process) => Current;
    }

    [Theory]
    [InlineData(@"F:\SteamLibrary\steamapps\common\Battlefield 6\bf6.exe", SoftwareKinds.Game)]
    [InlineData(@"f:/STEAMLIBRARY/steamapps/common/Battlefield 6/bin/../BF6.EXE", SoftwareKinds.Game)]
    [InlineData(@"D:\Other\bf6.exe", SoftwareKinds.RuntimeProduct)]
    [InlineData(@"F:\SteamLibrary\steamapps\common\Battlefield 6\helper.exe", SoftwareKinds.RuntimeProduct)]
    public void Match_WindowsGameListChangesOnlyCategoryForExactNormalizedPath(string path, string kind)
    {
        using var pipeline = CreatePipeline([], windowsGamePaths: new HashSet<string>
            { @"F:\SteamLibrary\steamapps\common\Battlefield 6\bf6.exe" });
        var result = pipeline.Match(new(46200, null, "bf6", path, false,
            ProductName: "Battlefield 6", CompanyName: "EA Digital Illusions CE AB"));

        Assert.Equal("runtime-product:ea-digital-illusions-ce-ab-battlefield-6", result.Id);
        Assert.Equal("Battlefield 6", result.Name);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(SoftwareDisplayKinds.Project(kind), result.DisplayKind);
        Assert.Equal(kind == SoftwareKinds.Game ? 95d : 35d,
            ResourceManager.App.Domain.Optimization.Scoring.OptimizationRuntimeScoringDefaults.BaseScoreForKind(result.Kind));
        Assert.Equal(kind == SoftwareKinds.Game ? @"Windows GameConfigStore\Children\MatchedExeFullPath" : null,
            result.CategorySource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Match_WindowsCategoryOverridesCatalogButExplicitUserKindWins(bool explicitKind)
    {
        const string path = @"D:\Apps\WPS\wps.exe";
        var catalog = SoftwareIdentityCatalogTestData.Create(new SoftwareIdentityCatalogEntry(
            "app-wps-office", "WPS Office", SoftwareKinds.Other, "test", ExecutableNames: ["wps.exe"]));
        var software = CreateSoftware("catalog:app-wps-office", "My Office", []) with
        {
            HasExplicitKind = explicitKind,
            Kind = SoftwareKinds.HighPerformance,
            DisplayKind = SoftwareDisplayKinds.HighPerformance
        };
        using var pipeline = CreatePipeline([software], identityCatalog: catalog,
            windowsGamePaths: new HashSet<string> { path });
        var result = pipeline.Match(new(46201, null, "wps", path, false));
        Assert.Equal(software.Id, result.Id);
        Assert.Equal(explicitKind ? software.Name : "WPS Office", result.Name);
        Assert.Equal(explicitKind ? SoftwareKinds.HighPerformance : SoftwareKinds.Game, result.Kind);
        Assert.Equal(!explicitKind, result.CategorySource is not null);
    }

    [Fact]
    public void Match_ExplicitManualClassificationWinsOverWindowsCategory()
    {
        var software = CreateSoftware("manual:word-memory", "WordMemory", [@"D:\Apps\WordMemory"]) with
            { HasExplicitKind = true };
        using var pipeline = CreatePipeline([software], windowsGamePaths: new HashSet<string>
            { @"D:\Apps\WordMemory\WordMemory.WebHost.exe" });
        var result = pipeline.Match(new(46202, null, "WordMemory.WebHost",
            @"D:\Apps\WordMemory\WordMemory.WebHost.exe", false));
        Assert.Equal(software.Id, result.Id);
        Assert.Equal(SoftwareKinds.Other, result.Kind);
        Assert.Null(result.CategorySource);
    }

    [Fact]
    public void Match_AdaptedAndControlledOwnershipPreservesWindowsGameCategory()
    {
        const string path = @"D:\Apps\WordMemory\WordMemory.WebHost.exe";
        var gamePaths = new HashSet<string> { path };
        var adapter = ScopedAdapter([new("WordMemory.WebHost", null, null)]);
        using var adapted = CreatePipeline([], [adapter], windowsGamePaths: gamePaths);
        var process = new RuntimeProcessIdentity(46203, null, "WordMemory.WebHost", path, false);
        Assert.Equal(SoftwareKinds.Game, adapted.Match(process).Kind);
        Assert.Equal(adapter.Id, adapted.Match(process).Id);
        var registration = new ControlledSoftwareRegistration(Guid.NewGuid(), DateTimeOffset.UtcNow,
            "test", ["WordMemory"], [@"D:\Apps\WordMemory"], [], [], "registered");
        using var controlled = CreatePipeline([], windowsGamePaths: gamePaths, controlledRegistrations: [registration]);
        Assert.Equal(SoftwareKinds.Game, controlled.Match(process).Kind);
        Assert.Equal($"controlled-registration:{registration.Id}", controlled.Match(process).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Match_WindowsCategoryPreservesPackageAndRootIdentity(bool package)
    {
        const string path = @"D:\Games\Portable\client.exe";
        var identity = new RuntimeSoftwareAttribution(package ? "package:game" : "runtime-root:game",
            "Portable Game", package ? SoftwareKinds.RuntimePackage : SoftwareKinds.RuntimeRoot,
            SoftwareDisplayKinds.General, [@"d:/games/portable"]);
        using var pipeline = new RuntimeProcessAttributionPipeline([], [], [],
            package ? new MatchedPackageIdentityResolver(identity) : new NullPackageIdentityResolver(),
            new NullServiceIdentityResolver(),
            package ? new NullRootIdentityResolver() : new MatchedRootIdentityResolver(identity),
            new NullSystemProcessClassifier(), SoftwareIdentityCatalogTestData.Empty,
            SoftwareIdentityCatalogTestData.Owner, new HashSet<string> { path });
        var result = pipeline.Match(new(46204, null, "client", path, false));
        Assert.Equal(identity.Id, result.Id);
        Assert.Equal(identity.Name, result.Name);
        Assert.Equal(identity.RootPaths, result.RootPaths);
        Assert.Equal(SoftwareKinds.Game, result.Kind);
        Assert.NotNull(result.CategorySource);
    }

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
        Assert.Equal(SoftwareKinds.Other, result.Kind);
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
        using var pipeline = CreatePipeline([], identityCatalog: identityCatalog);

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
        using var pipeline = CreatePipeline([
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
        using var pipeline = CreatePipeline([
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
        using var pipeline = CreatePipeline([
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
        using var pipeline = CreatePipeline([
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
        using var pipeline = CreatePipeline([
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
        using var pipeline = CreatePipeline([]);
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
        using var pipeline = CreatePipeline([], [adapter]);
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
        using var pipeline = CreatePipeline([], [adapter]);
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
        using var pipeline = CreatePipeline([
            CreateSoftware("windows-installed:foo-bar", "Foo Bar", [@"D:\Tools"])
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
        using var pipeline = CreatePipeline([
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
        using var pipeline = CreatePipeline(
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

    [Theory]
    [InlineData(null)]
    [InlineData(@"D:\Other\WordMemory.WebHost.exe")]
    [InlineData(@"D:\Apps\WordMemory2\WordMemory.WebHost.exe")]
    [InlineData(@"D:\Apps\WordMemory\..\Other\WordMemory.WebHost.exe")]
    public void AdapterNameCannotClaimAProcessOutsideItsProgramRoot(string? path)
    {
        var adapter = ScopedAdapter([new("WordMemory.WebHost", null, null)]);
        using var pipeline = CreatePipeline([], [adapter]);
        var attribution = pipeline.Match(new(8001, null, "WordMemory.WebHost", path, false, ProductName: "WordMemory"));
        Assert.NotEqual(adapter.Id, attribution?.Id);
    }

    [Fact]
    public void LegacyExecutableDeclarationCannotExpandAdapterRoots()
    {
        const string outside = @"D:\Other\WordMemory.WebHost.exe";
        var adapter = ScopedAdapter([new("WordMemory.WebHost", null, outside)]);
        using var pipeline = CreatePipeline([], [adapter]);
        Assert.NotEqual(adapter.Id, pipeline.Match(new(8001, null, "WordMemory.WebHost", outside, false))?.Id);
        Assert.Equal(adapter.Id, pipeline.Match(new(8002, null, "UndeclaredHelper", @"D:\Apps\WordMemory\bin\helper.exe", false))?.Id);
    }

    [Fact]
    public void ExactLivePidAndStartKeyCannotClaimAnotherProgramsImage()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var start = current.StartTime.ToUniversalTime().Ticks;
        var adapter = ScopedAdapter([new("WordMemory.WebHost", current.Id, null)]);
        using var pipeline = CreatePipeline([], [adapter]);
        Assert.NotEqual(adapter.Id, pipeline.Match(new(current.Id, null, "WordMemory.WebHost", Environment.ProcessPath, false, StartKey: start))?.Id);
        Assert.NotEqual(adapter.Id, pipeline.Match(new(current.Id, null, "WordMemory.WebHost", null, false, StartKey: start))?.Id);
    }

    private static AdapterSoftwareRegistration ScopedAdapter(IReadOnlyList<AdapterProcessDeclaration> processes)
        => new("adapter:scope-fixture", AdapterRegistrationSchemaVersions.Current,
            "fixture.adapter", "fixture", "WordMemory", null, [@"D:\Apps\WordMemory"], processes, [],
            new(AdapterResourceMarkerTransports.LoopbackHttp, "http://127.0.0.1:19090/ledger"),
            new(AdapterResourceMarkerStates.Online, DateTimeOffset.UtcNow, 200, "ok"),
            "registered", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static RuntimeProcessAttributionPipeline CreatePipeline(
        IReadOnlyList<SoftwareRecord> software,
        IReadOnlyList<AdapterSoftwareRegistration>? adapterRegistrations = null,
        ResourceManager.App.Application.SoftwareIdentity.ISoftwareIdentityCatalog? identityCatalog = null,
        IRuntimePackageIdentityResolver? packageResolver = null,
        IRuntimeServiceIdentityResolver? serviceResolver = null,
        IReadOnlySet<string>? windowsGamePaths = null,
        IReadOnlyList<ControlledSoftwareRegistration>? controlledRegistrations = null,
        IRuntimeRootIdentityResolver? rootResolver = null)
    {
        return new RuntimeProcessAttributionPipeline(
            software,
            adapterRegistrations ?? [],
            controlledRegistrations ?? [],
            packageResolver ?? new NullPackageIdentityResolver(),
            serviceResolver ?? new NullServiceIdentityResolver(),
            rootResolver ?? new NullRootIdentityResolver(),
            new NullSystemProcessClassifier(),
            identityCatalog ?? SoftwareIdentityCatalogTestData.Empty,
            SoftwareIdentityCatalogTestData.Owner,
            windowsGamePaths);
    }

    private static SoftwareRecord CreateSoftware(string id, string name, IReadOnlyList<string> rootPaths)
    {
        return new SoftwareRecord(
            id,
            name,
            SoftwareKinds.Other,
            "other",
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

    private sealed class MatchedPackageIdentityResolver(RuntimeSoftwareAttribution attribution) : IRuntimePackageIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.Matched(attribution);
    }

    private sealed class MatchedRootIdentityResolver(RuntimeSoftwareAttribution attribution) : IRuntimeRootIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.Matched(attribution);
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
