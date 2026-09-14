using ResourceManager.Shared.BrowserRuntimes;

namespace Resource_Manager_APP.Tests;

public sealed class BrowserRuntimeSelectionTests
{
    [Theory]
    [InlineData("Opera Internet Browser", "opera", true)]
    [InlineData("Microsoft Windows Operating System", "opera", false)]
    [InlineData(@"C:\Apps\Arc\Arc.exe", "arc", true)]
    [InlineData("ArcGIS Pro", "arc", false)]
    [InlineData(@"C:\Apps\360Chrome\Chrome\Application\360chrome.exe", "360chrome", true)]
    [InlineData(@"D:\Software\Quark\quark.exe", "quark", true)]
    public void BrowserEvidence_RequiresTokenBoundaries(
        string evidence,
        string token,
        bool expected)
    {
        Assert.Equal(expected, BrowserRuntimeEvidence.ContainsBoundedToken(evidence, token));
    }

    [Fact]
    public void Select_PrefersMachineSharedRuntimeOverNewerAlternatives()
    {
        var result = BrowserRuntimeSelection.Select(
        [
            Candidate("managed", BrowserRuntimeKinds.WebView2Runtime, "200.0.0.0", "Managed"),
            Candidate("user", BrowserRuntimeKinds.WebView2Runtime, "190.0.0.0", "SystemUser"),
            Candidate("machine", BrowserRuntimeKinds.WebView2Runtime, "100.0.0.0", "SystemMachine")
        ]);

        Assert.Equal("machine", result.SharedRuntime?.Id);
        Assert.True(result.Candidates.Single(item => item.Id == "machine").Selected);
        Assert.False(result.Candidates.Single(item => item.Id == "managed").Selected);
    }

    [Fact]
    public void Select_UsesVersionInsteadOfBrowserBrand()
    {
        var result = BrowserRuntimeSelection.Select(
        [
            Candidate("chrome", BrowserRuntimeKinds.ChromiumBrowser, "120.0.0.0", "KnownInstall", "Google Chrome"),
            Candidate("vivaldi", BrowserRuntimeKinds.ChromiumBrowser, "130.0.0.0", "KnownInstall", "Vivaldi"),
            Candidate("edge", BrowserRuntimeKinds.ChromiumBrowser, "125.0.0.0", "KnownInstall", "Microsoft Edge")
        ]);

        Assert.Equal("vivaldi", result.BrowserFallback?.Id);
        Assert.True(result.Candidates.Single(item => item.Id == "vivaldi").Selected);
        Assert.False(result.Candidates.Single(item => item.Id == "chrome").Selected);
    }

    [Fact]
    public void Select_MarksSharedAndBrowserSelectionsIndependently()
    {
        var result = BrowserRuntimeSelection.Select(
        [
            Candidate("runtime", BrowserRuntimeKinds.WebView2Runtime, "125.0.0.0", "SystemMachine"),
            Candidate("browser", BrowserRuntimeKinds.ChromiumBrowser, "130.0.0.0", "BrowserRegistration")
        ]);

        Assert.Equal(["runtime", "browser"],
            result.Candidates.Where(static item => item.Selected).Select(static item => item.Id).ToArray());
        Assert.True(result.SharedRuntime?.NativeWebView2Compatible);
        Assert.False(result.BrowserFallback?.NativeWebView2Compatible);
    }

    [Fact]
    public void Select_PrefersChromiumEngineBeforeGecko()
    {
        var result = BrowserRuntimeSelection.Select(
        [
            Candidate("firefox", BrowserRuntimeKinds.GeckoBrowser, "999.0", "BrowserRegistration"),
            Candidate("360", BrowserRuntimeKinds.ChromiumBrowser, "132.0", "BrowserRegistration", "360 安全浏览器")
        ]);

        Assert.Equal("360", result.BrowserFallback?.Id);
    }

    [Fact]
    public void Select_UsesGeckoWhenNoChromiumBrowserExists()
    {
        var result = BrowserRuntimeSelection.Select(
        [
            Candidate("firefox", BrowserRuntimeKinds.GeckoBrowser, "141.0", "BrowserRegistration"),
            Candidate("waterfox", BrowserRuntimeKinds.GeckoBrowser, "140.0", "KnownInstall")
        ]);

        Assert.Equal("firefox", result.BrowserFallback?.Id);
        Assert.True(result.Candidates.Single(item => item.Id == "firefox").Selected);
    }

    [Theory]
    [InlineData("quark.exe Quark", BrowserRuntimeKinds.ChromiumBrowser)]
    [InlineData("360chrome.exe 360 极速浏览器", BrowserRuntimeKinds.ChromiumBrowser)]
    [InlineData("360safe.exe 360 安全卫士", null)]
    public void ResolveBrowserKind_ClassifiesDomesticBrowserEvidenceOnly(
        string evidence,
        string? expected)
    {
        Assert.Equal(expected, BrowserRuntimeDiscovery.ResolveBrowserKind(
            @"C:\runtime-tests\browser.exe",
            evidence));
    }

    [Fact]
    public void ResolveBrowserKind_RequiresGeckoBrowserEvidenceAndLayout()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"browser-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var executablePath = Path.Combine(directory, "firefox.exe");
            foreach (var fileName in new[] { "firefox.exe", "xul.dll", "mozglue.dll", "omni.ja" })
            {
                File.WriteAllBytes(Path.Combine(directory, fileName), []);
            }

            Assert.Equal(
                BrowserRuntimeKinds.GeckoBrowser,
                BrowserRuntimeDiscovery.ResolveBrowserKind(executablePath, "firefox.exe Mozilla Firefox"));
            Assert.Null(BrowserRuntimeDiscovery.ResolveBrowserKind(executablePath, "thunderbird.exe Thunderbird"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static BrowserRuntimeCandidate Candidate(
        string id,
        string kind,
        string version,
        string source,
        string? name = null)
        => new(
            id,
            name ?? id,
            kind,
            version,
            $@"C:\runtime-tests\{id}\browser.exe",
            $@"C:\runtime-tests\{id}",
            source,
            NativeWebView2Compatible: kind == BrowserRuntimeKinds.WebView2Runtime);
}
