using ResourceManager.NativeUi.WebView;
using ResourceManager.Shared.Localization;
using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class BrowserRuntimeStartupTests
{
    [Fact]
    public async Task DeclinedInstallationRecordsReasonAndExitsShellWithoutDegradedState()
    {
        foreach (var language in AppLanguage.SupportedIds)
        {
            var reason = ToolText.For(language).WebViewInstallCancelled;
            var failure = new BrowserRuntimeInstallationDeclinedException(reason);
            var calls = new List<string>();
            Assert.False(await MainForm.InitializeFrontendHostAsync(
                () => Task.FromException(failure),
                exception => { Assert.Same(failure, exception); calls.Add(exception.Message); },
                () => calls.Add("exit"),
                () => calls.Add("degraded")));
            Assert.Equal([reason, "exit"], calls);
        }
    }

    [Fact]
    public async Task OrdinaryInitializationFailureKeepsDegradedShell()
    {
        var calls = new List<string>();
        Assert.False(await MainForm.InitializeFrontendHostAsync(
            () => Task.FromException(new IOException("failure")),
            _ => calls.Add("record"), () => calls.Add("exit"), () => calls.Add("degraded")));
        Assert.Equal(["record", "degraded"], calls);
    }

    [Fact]
    public void NativeConsentUsesSelectedLanguageAndDefaultsToCancel()
    {
        foreach (var language in AppLanguage.SupportedIds)
        {
            var text = ToolText.For(language);
            var page = BrowserRuntimeResolver.CreateConsentPage(text);
            Assert.Equal(text.WebViewInstallTitle, page.Heading);
            Assert.Equal(text.WebViewInstallConsent, page.Text);
            Assert.Equal(text.WebViewInstall, page.Buttons[0].Text);
            Assert.Equal(text.Cancel, page.Buttons[1].Text);
            Assert.Same(page.Buttons[1], page.DefaultButton);
            Assert.True(page.AllowCancel);
        }
    }

    [Fact]
    public async Task DeclinedConsentNeverInvokesInstallerOrSecondProbe()
    {
        var probes = 0;
        var installs = 0;
        var consents = 0;
        await Assert.ThrowsAsync<BrowserRuntimeInstallationDeclinedException>(() => BrowserRuntimeResolver.ResolveAsync(
            () => { probes++; return null; },
            () => { consents++; return false; },
            () => { installs++; return Task.CompletedTask; }));
        Assert.Equal(1, probes);
        Assert.Equal(1, consents);
        Assert.Equal(0, installs);
    }

    [Fact]
    public async Task InstalledSystemRuntimeDoesNotDownloadOrInstall()
    {
        var installs = 0;
        var runtime = await BrowserRuntimeResolver.ResolveAsync(() => "153.0.0.0", () => throw new Exception("Installed runtime must not request consent."), () =>
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
        var consents = 0;
        var installs = 0;
        var runtime = await BrowserRuntimeResolver.ResolveAsync(() => { probes++; return version; }, () => { consents++; return true; }, () =>
        {
            installs++;
            version = "153.0.0.0";
            return Task.CompletedTask;
        });
        Assert.Equal(1, installs);
        Assert.Equal(2, probes);
        Assert.Equal(1, consents);
        Assert.Equal("153.0.0.0", runtime.Version);
        Assert.Null(runtime.BrowserExecutableFolder);
    }

    [Fact]
    public async Task InstallerFailureIsNotReportedAsReadyOrRetried()
    {
        var installs = 0;
        await Assert.ThrowsAsync<IOException>(() => BrowserRuntimeResolver.ResolveAsync(() => null, () => true, () =>
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => BrowserRuntimeResolver.ResolveAsync(() => null, () => true, () =>
        {
            installs++;
            return Task.CompletedTask;
        }));
        Assert.Equal(1, installs);
    }
}
