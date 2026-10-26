using ResourceManager.NativeUi;
using ResourceManager.NativeUi.Blocks;

namespace Resource_Manager_APP.Tests;

[CollectionDefinition("Native UI product composition", DisableParallelization = true)]
public sealed class NativeUiProductCompositionCollection { }

[Collection("Native UI product composition")]
public sealed class NativeUiProductCompositionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(0x0011, 0)]
    [InlineData(0x0016, 1)]
    public async Task ProductWindowAcceptsSystemEndMessageAndStartsTheSystemExitPath(uint message, int confirmed)
    {
        await using var app = await NativeUiProductTestHost.StartAsync(slowBrowser: true);
        var browser = await app.NextBrowserAsync();
        await browser.Initialized.Task.WaitAsync(Limit);
        nint accepted = 0;
        var window = app.Window;
        await app.InvokeAsync(() => accepted = SendMessage(window.Handle, message, confirmed, 0));
        if (message == 0x0011) Assert.NotEqual(IntPtr.Zero, accepted);
        else Assert.Equal(IntPtr.Zero, accepted);
        await browser.SurfaceClosed.Task.WaitAsync(Limit);
        var shutdown = app.Trees.Read().Facts.Single(fact => fact.Key == "program:shutting-down");
        Assert.Contains(shutdown.Reasons, reason => reason.ConfigurationOrDependency == "windows-session-ending");
        Assert.False(app.Exited.IsCompleted);
        await app.InvokeAsync(() => Assert.False(window.IsDisposed));
        app.ReleaseBrowser.SetResult();
        await app.Exited.WaitAsync(Limit);
    }

    [Fact]
    public async Task CanceledEndSessionKeepsTheActualApplicationAndWindowRunning()
    {
        await using var app = await NativeUiProductTestHost.StartAsync();
        var browser = await app.NextBrowserAsync();
        await browser.Initialized.Task.WaitAsync(Limit);
        await app.InvokeAsync(() => Assert.Equal(IntPtr.Zero, SendMessage(app.Window.Handle, 0x0016, 0, 0)));
        Assert.False(app.Trees.Read().ShuttingDown);
        Assert.False(app.Exited.IsCompleted);
        Assert.False(browser.SurfaceClosed.Task.IsCompleted);
        Assert.Equal(BusinessBlockState.Running, app.Trees.ReadState(NativeUiBlockIds.WebViewHost));
    }

    [Fact]
    public async Task FailedWindowPreparationReleasesItsOwnedFormBeforeTheReplacementStarts()
    {
        await using var app = await NativeUiProductTestHost.StartAsync(failFirstPresentation: true);
        var failed = await app.NextBrowserAsync();
        await failed.SurfaceClosed.Task.WaitAsync(Limit);
        var replacement = await app.NextBrowserAsync();
        await replacement.Initialized.Task.WaitAsync(Limit);
        Assert.True(failed.View.IsDisposed);
        await app.InvokeAsync(() => Assert.True(app.PresentedWindows.First().IsDisposed));
        Assert.NotSame(failed, replacement);
        Assert.True(replacement.VisibleWhenInitialized);
    }

    [Theory]
    [InlineData("tray")]
    [InlineData("instance")]
    [InlineData("session")]
    public async Task RealExitEntryWaitsForBothTreesBeforeMessageLoopReturns(string entry)
    {
        await using var app = await NativeUiProductTestHost.StartAsync(slowBrowser: true);
        var browser = await app.NextBrowserAsync();
        await browser.Initialized.Task.WaitAsync(Limit);
        await app.Trees.WaitForStateAsync(NativeUiBlockIds.TrayRoot, BusinessBlockState.Running).WaitAsync(Limit);
        switch (entry)
        {
            case "tray": await app.TrayExitAsync(); break;
            case "instance": await app.SendCommandAsync("exit"); break;
            case "session": await app.SessionEndingAsync(); break;
            default: throw new InvalidOperationException(entry);
        }
        await browser.SurfaceClosed.Task.WaitAsync(Limit);
        Assert.True(app.Trees.Read().ShuttingDown);
        Assert.False(app.Exited.IsCompleted);
        Assert.Equal(BusinessBlockState.Stopping, app.Trees.ReadState(NativeUiBlockIds.WebViewHost));
        app.ReleaseBrowser.TrySetResult();
        await app.Exited.WaitAsync(Limit);
        Assert.NotNull(app.ExitSnapshot);
        Assert.All(app.ExitSnapshot.Blocks, block => Assert.Equal(BusinessBlockState.Stopped, block.State));
    }

    [Fact]
    public async Task ProductWindowWaitsForBrowserBeforeProxyAndReopensFreshThroughInstanceCommand()
    {
        await using var app = await NativeUiProductTestHost.StartAsync(slowBrowser: true);
        var firstBrowser = await app.NextBrowserAsync();
        await firstBrowser.Initialized.Task.WaitAsync(Limit);
        Assert.True(firstBrowser.VisibleWhenInitialized);
        var firstProxy = app.Proxy;
        var firstWindow = app.Window;
        await app.WindowCloseAsync();
        await firstBrowser.SurfaceClosed.Task.WaitAsync(Limit);
        Assert.Equal(BusinessBlockState.Running, app.Trees.ReadState(NativeUiBlockIds.LoopbackProxy));
        Assert.Equal(BusinessBlockState.Running, app.Trees.ReadState(NativeUiBlockIds.TrayRoot));
        Assert.Equal(BusinessBlockState.Running, app.Trees.ReadState(NativeUiBlockIds.BackendConnection));
        await app.InvokeAsync(() => Assert.False(firstWindow.IsDisposed));
        await app.SendCommandAsync("show-passive");
        Assert.False(app.Browsers.Reader.TryPeek(out _));
        app.ReleaseBrowser.TrySetResult();
        var secondBrowser = await app.NextBrowserAsync();
        await secondBrowser.Initialized.Task.WaitAsync(Limit);
        Assert.NotSame(firstBrowser, secondBrowser);
        Assert.NotSame(firstProxy, app.Proxy);
        Assert.NotSame(firstWindow, app.Window);
        await app.InvokeAsync(() => Assert.True(firstWindow.IsDisposed));
        Assert.True(secondBrowser.VisibleWhenInitialized);
    }

    [Fact]
    public async Task ProductWindowDefersWebViewUntilFirstReadyThenKeepsItDuringBackendLoss()
    {
        await using var app = await NativeUiProductTestHost.StartAsync(delayedConnection: true, pauseReconnect: true);
        var browser = await app.NextBrowserAsync();
        await app.Trees.WaitForStateAsync(NativeUiBlockIds.MainWindow, BusinessBlockState.Running).WaitAsync(Limit);
        await app.Trees.WaitForStateAsync(NativeUiBlockIds.LoopbackProxy, BusinessBlockState.Running).WaitAsync(Limit);
        Assert.Equal(BusinessBlockState.Stopped, app.Trees.ReadState(NativeUiBlockIds.WebViewHost));
        Assert.False(browser.Initialized.Task.IsCompleted);
        app.AllowConnect.SetResult();
        await browser.Initialized.Task.WaitAsync(Limit);
        Assert.True(browser.VisibleWhenInitialized);
        var first = await app.NextSessionAsync();
        first.Loss.SetResult(BackendServiceUnavailableEventArgs.OwnedProcessExited(101));
        var second = await app.NextSessionAsync();
        Assert.NotSame(first.Session, second.Session);
        Assert.Equal(BusinessBlockState.Running, app.Trees.ReadState(NativeUiBlockIds.WebViewHost));
        Assert.False(browser.SurfaceClosed.Task.IsCompleted);
        Assert.False(app.Browsers.Reader.TryPeek(out _));
        app.AllowReconnect.SetResult();
    }

    [Fact]
    public async Task ProductConnectionAndMainTreeFinishWhileIndependentTrayStillHoldsOldSession()
    {
        await using var app = await NativeUiProductTestHost.StartAsync(slowTray: true);
        var browser = await app.NextBrowserAsync();
        await browser.Initialized.Task.WaitAsync(Limit);
        var first = await app.NextSessionAsync();
        for (var count = 0; count < 3; count++)
        {
            var consumer = await app.Consumers.Reader.ReadAsync().AsTask().WaitAsync(Limit);
            Assert.Same(first.Session, consumer.Session);
        }
        first.Loss.SetResult(BackendServiceUnavailableEventArgs.OwnedProcessExited(102));
        await app.TrayStopping.Task.WaitAsync(Limit);
        var second = await app.NextSessionAsync();
        Assert.NotSame(first.Session, second.Session);
        Assert.All(first.Handlers, handler => Assert.Equal(0, handler.Disposals));
        await app.InvokeAsync(app.Context.ExitThread);
        await app.Trees.WaitForStateAsync(NativeUiBlockIds.MainRoot, BusinessBlockState.Stopped).WaitAsync(Limit);
        Assert.False(app.Exited.IsCompleted);
        Assert.Equal(BusinessBlockState.Stopping, app.Trees.ReadState(NativeUiBlockIds.TrayNotifications));
        Assert.All(first.Handlers, handler => Assert.Equal(0, handler.Disposals));
        app.ReleaseTray.SetResult();
        await app.Exited.WaitAsync(Limit);
        Assert.All(first.Handlers, handler => Assert.Equal(1, handler.Disposals));
        Assert.All(second.Handlers, handler => Assert.Equal(1, handler.Disposals));
    }

    [Fact]
    public async Task ProductBackgroundStartupDoesNotCreateWindowBeforeShowCommand()
    {
        await using var app = await NativeUiProductTestHost.StartAsync(startInBackground: true);
        await app.Trees.WaitForStateAsync(NativeUiBlockIds.BackendConnection, BusinessBlockState.Running).WaitAsync(Limit);
        await app.Trees.WaitForStateAsync(NativeUiBlockIds.TrayRoot, BusinessBlockState.Running).WaitAsync(Limit);
        Assert.Equal(BusinessBlockState.Stopped, app.Trees.ReadState(NativeUiBlockIds.MainWindow));
        Assert.False(app.Browsers.Reader.TryPeek(out _));
        await app.SendCommandAsync("show-passive");
        var browser = await app.NextBrowserAsync();
        await browser.Initialized.Task.WaitAsync(Limit);
        Assert.True(browser.VisibleWhenInitialized);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
