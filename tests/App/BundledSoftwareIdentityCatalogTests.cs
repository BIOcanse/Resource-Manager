using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.SoftwareIdentity;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class BundledSoftwareIdentityCatalogTests
{
    [Fact]
    public void CommonApplicationsAndTheirSpecificHelpersShareTheExpectedIdentity()
    {
        using var owner = HostManagerTestPlanFactory.CreateSoftwareIdentityOwner();
        using var catalog = new BundledSoftwareIdentityCatalog(new CatalogEnvironment(), owner);
        (string Id, string[] Executables)[] products =
        [
            ("app-openai-codex", ["codex.exe", "codex-code-mode-host.exe", "codex-computer-use-swift.exe", "codex-windows-sandbox-service.exe", "codex-command-runner.exe"]),
            ("app-claude", ["claude.exe", "cowork-svc.exe"]),
            ("app-windows-terminal", ["WindowsTerminal.exe"]),
            ("app-wetype", ["wetype_renderer.exe", "wetype_server.exe", "wetype_update.exe"]),
            ("app-sogou-input", ["SogouCloud.exe", "SOGOUSmartAssistant.exe"]),
            ("app-spotify", ["SpotifyLauncher.exe"]),
            ("app-nahimic", ["Nahimic3.exe", "nahimicNotifSys.exe"]),
            ("app-senary-smart-audio", ["SenaryAudioApp.exe"]),
            ("app-saku-overclock", ["Saku Overclock.exe"]),
            ("app-xbox", ["XboxPcAppFT.exe"]),
            ("app-amd-software", ["amdow.exe", "AMDRSServ.exe", "AMDRSSrcExt.exe"]),
            ("app-nvidia-app", ["NVIDIA Overlay.exe", "nvsphelper64.exe"]),
            ("app-mihoyo-launcher", ["HYPHelper.exe"])
        ];
        foreach (var (id, executables) in products)
        {
            foreach (var executable in executables)
            {
                var process = Process(executable);
                var match = catalog.MatchProcess(process);
                Assert.Equal(id, match?.Id);
                Assert.Equal(SoftwareKinds.Other, match?.Kind);
                Assert.Equal(35, OptimizationRuntimeScoringDefaults.BaseScoreForKind(match!.Kind));
                Assert.Equal(id, catalog.MatchPortableProcess(process)?.Entry.Id);
            }
        }
    }

    [Theory]
    [InlineData("OpenAI.Codex_2p2nqsd0c76g0", "app-openai-codex", SoftwareKinds.Other)]
    [InlineData("Claude_pzs8sxrjxfjjc", "app-claude", SoftwareKinds.Other)]
    [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe", "app-windows-terminal", SoftwareKinds.Other)]
    [InlineData("A-Volute.Nahimic_w2gh52qy24etm", "app-nahimic", SoftwareKinds.Other)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "app-spotify", SoftwareKinds.Other)]
    [InlineData("SenaryTechnologyLimited.SenarySmartAudio_dqz7eftfn33jw", "app-senary-smart-audio", SoftwareKinds.Other)]
    [InlineData("Microsoft.GamingApp_8wekyb3d8bbwe", "app-xbox", SoftwareKinds.Other)]
    [InlineData("Microsoft.GamingServices_8wekyb3d8bbwe", "windows-gaming-services", SoftwareKinds.WindowsService)]
    [InlineData("MicrosoftWindows.CrossDevice_cw5n1h2txyewy", "windows-cross-device", SoftwareKinds.WindowsComponent)]
    [InlineData("MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy", "windows-widgets", SoftwareKinds.WindowsComponent)]
    [InlineData("Microsoft.WidgetsPlatformRuntime_8wekyb3d8bbwe", "windows-widgets-platform", SoftwareKinds.WindowsComponent)]
    [InlineData("Microsoft.StartExperiencesApp_8wekyb3d8bbwe", "windows-start-experiences", SoftwareKinds.WindowsComponent)]
    [InlineData("Microsoft.PowerShell", "app-powershell", SoftwareKinds.Other)]
    [InlineData("Tencent.WeType", "app-wetype", SoftwareKinds.Other)]
    [InlineData("Sogou.SogouInput", "app-sogou-input", SoftwareKinds.Other)]
    public void PackageIdentityWorksWithoutADisplayNameOrExecutable(string package, string id, string kind)
    {
        using var owner = HostManagerTestPlanFactory.CreateSoftwareIdentityOwner();
        using var catalog = new BundledSoftwareIdentityCatalog(new CatalogEnvironment(), owner);
        var installed = new InstalledSoftwareEntry($"appx:{package}", "Unrecognized display name", null, null, null, [], null, string.Empty);
        var match = catalog.MatchInstalledSoftware(installed);
        Assert.Equal(id, match?.Id);
        Assert.Equal(kind, match?.Kind);
    }

    [Fact]
    public void WindowsComponentsAndServicesKeepTheirOwnCategoryRatherThanBecomingGames()
    {
        using var owner = HostManagerTestPlanFactory.CreateSoftwareIdentityOwner();
        using var catalog = new BundledSoftwareIdentityCatalog(new CatalogEnvironment(), owner);
        foreach (var executable in new[] { "gamingservices.exe", "gamingservicesnet.exe", "GameInputRedistService.exe" })
        {
            var match = catalog.MatchProcess(Process(executable));
            Assert.Equal(SoftwareKinds.WindowsService, match?.Kind);
            Assert.Equal(90, OptimizationRuntimeScoringDefaults.BaseScoreForKind(match!.Kind));
        }
        foreach (var executable in new[] { "CrossDeviceService.exe", "WidgetBoard.exe", "MicrosoftStartFeedProvider.exe" })
        {
            var match = catalog.MatchProcess(Process(executable));
            Assert.Equal(SoftwareKinds.WindowsComponent, match?.Kind);
            Assert.Equal(90, OptimizationRuntimeScoringDefaults.BaseScoreForKind(match!.Kind));
        }
    }

    [Fact]
    public void SharedNamesRequireProductMetadataInsteadOfClaimingAProductFromTheFileName()
    {
        using var owner = HostManagerTestPlanFactory.CreateSoftwareIdentityOwner();
        using var catalog = new BundledSoftwareIdentityCatalog(new CatalogEnvironment(), owner);
        // The observed desktop app is ChatGPT.exe inside OpenAI.Codex, with ProductName Codex.
        Assert.Equal("app-powershell", catalog.MatchProcess(Process("pwsh.exe") with { ProductName = "PowerShell" })?.Id);
        Assert.Equal("app-openai-codex", catalog.MatchProcess(Process("ChatGPT.exe") with { ProductName = "Codex" })?.Id);
        Assert.Equal("app-sogou-input", catalog.MatchProcess(Process("SGTool.exe") with { ProductName = "搜狗输入法" })?.Id);
        Assert.Equal("app-amd-software", catalog.MatchProcess(Process("CPUMetricsServer.exe") with { ProductName = "Radeon Settings" })?.Id);
        Assert.Equal("app-amd-software", catalog.MatchProcess(Process("cncmd.exe") with { ProductName = "Radeon Software" })?.Id);
        Assert.Equal("app-windows-terminal", catalog.MatchProcess(Process("OpenConsole.exe") with { ProductName = "Windows Terminal" })?.Id);
        Assert.Equal("windows-widgets-platform", catalog.MatchProcess(Process("WidgetService.exe") with { ProductName = "Widgets Platform Runtime" })?.Id);
    }

    [Theory]
    [InlineData("ChatGPT.exe")]
    [InlineData("SGTool.exe")]
    [InlineData("CPUMetricsServer.exe")]
    [InlineData("cncmd.exe")]
    [InlineData("OpenConsole.exe")]
    [InlineData("WidgetService.exe")]
    [InlineData("nvcontainer.exe")]
    [InlineData("GCUService.exe")]
    [InlineData("OSDTpDetect.exe")]
    [InlineData("node.exe")]
    [InlineData("node_repl.exe")]
    [InlineData("dotnet.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("bash.exe")]
    [InlineData("msedgewebview2.exe")]
    [InlineData("RuntimeBroker.exe")]
    [InlineData("crashpad_handler.exe")]
    [InlineData("UnityCrashHandler64.exe")]
    [InlineData("ZFGameBrowser.exe")]
    [InlineData("grep.exe")]
    [InlineData("head.exe")]
    [InlineData("tail.exe")]
    [InlineData("sleep.exe")]
    public void SharedHostsAndShellUtilitiesHaveNoStandaloneProductAlias(string executable)
    {
        using var owner = HostManagerTestPlanFactory.CreateSoftwareIdentityOwner();
        using var catalog = new BundledSoftwareIdentityCatalog(new CatalogEnvironment(), owner);
        Assert.Null(catalog.MatchProcess(Process(executable)));
        Assert.Null(catalog.MatchPortableProcess(Process(executable)));
    }

    private static RuntimeProcessIdentity Process(string executable) =>
        new(42, null, Path.GetFileNameWithoutExtension(executable), Path.Combine(@"D:\Portable", executable), false);

    private sealed class CatalogEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}


