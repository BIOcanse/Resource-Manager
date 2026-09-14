using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

public sealed class WebViewDeveloperOptionsTests
{
    [Fact]
    public void DeveloperOptionsExposeOnlyAnExplicitValidLoopbackPort()
    {
        const string baseArguments = "--disable-background-networking";

        Assert.Equal(
            baseArguments,
            WebViewDeveloperOptions.AddLoopbackRemoteDebugging(baseArguments, null));
        Assert.Equal(
            "--disable-background-networking --remote-debugging-address=127.0.0.1 --remote-debugging-port=9332",
            WebViewDeveloperOptions.AddLoopbackRemoteDebugging(baseArguments, "9332"));
        Assert.Throws<InvalidOperationException>(() =>
            WebViewDeveloperOptions.AddLoopbackRemoteDebugging(baseArguments, "0"));
        Assert.Throws<InvalidOperationException>(() =>
            WebViewDeveloperOptions.AddLoopbackRemoteDebugging(baseArguments, "not-a-port"));
    }

    [Fact]
    public void DeveloperOptionsExposeOnlyAnExplicitBoundedPerformanceBootstrap()
    {
        Assert.Null(
            WebViewDeveloperOptions.CreateFrontendPerformanceBootstrapScript(null));
        var script = WebViewDeveloperOptions.CreateFrontendPerformanceBootstrapScript("50000");
        Assert.NotNull(script);
        Assert.Contains("schemaVersion: 1", script, StringComparison.Ordinal);
        Assert.Contains("capacity: 50000", script, StringComparison.Ordinal);
        Assert.DoesNotContain("token", script, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() =>
            WebViewDeveloperOptions.CreateFrontendPerformanceBootstrapScript("255"));
        Assert.Throws<InvalidOperationException>(() =>
            WebViewDeveloperOptions.CreateFrontendPerformanceBootstrapScript("100001"));
        Assert.Throws<InvalidOperationException>(() =>
            WebViewDeveloperOptions.CreateFrontendPerformanceBootstrapScript("50000.0"));
    }
}
