using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

public sealed class BrowserRuntimeStartupTests
{
    [Fact]
    public async Task InstalledSystemRuntimeDoesNotDownloadOrInstall()
    {
        var installs = 0;
        var runtime = await BrowserRuntimeResolver.ResolveAsync(() => "153.0.0.0", () =>
        {
            installs++;
            return Task.CompletedTask;
        });
        Assert.Equal(0, installs);
        Assert.Equal("SystemShared", runtime.Source);
        Assert.Null(runtime.BrowserExecutableFolder);
        Assert.Equal("153.0.0.0", runtime.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingSystemRuntimeInstallsOnceThenProbesAgain(string? initialVersion)
    {
        var version = initialVersion;
        var probes = 0;
        var installs = 0;
        var runtime = await BrowserRuntimeResolver.ResolveAsync(() => { probes++; return version; }, () =>
        {
            installs++;
            version = "153.0.0.0";
            return Task.CompletedTask;
        });
        Assert.Equal(1, installs);
        Assert.Equal(2, probes);
        Assert.Equal("153.0.0.0", runtime.Version);
        Assert.Null(runtime.BrowserExecutableFolder);
    }

    [Fact]
    public async Task InstallerFailureIsNotReportedAsReadyOrRetried()
    {
        var installs = 0;
        await Assert.ThrowsAsync<IOException>(() => BrowserRuntimeResolver.ResolveAsync(() => null, () =>
        {
            installs++;
            throw new IOException("Download failed.");
        }));
        Assert.Equal(1, installs);
    }

    [Fact]
    public async Task SuccessfulInstallerExitStillRequiresAvailableRuntime()
    {
        var installs = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => BrowserRuntimeResolver.ResolveAsync(() => null, () =>
        {
            installs++;
            return Task.CompletedTask;
        }));
        Assert.Equal(1, installs);
    }
}
