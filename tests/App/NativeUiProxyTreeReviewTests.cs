using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.NativeUi.Blocks;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiProxyTreeReviewTests
{
    [Fact]
    public async Task BrowserBranchDrainsBeforeItsProxyReceivesStop()
    {
        var entered = Signal();
        var stopBrowser = Signal();
        var releaseBrowser = Signal();
        var stopProxy = Signal();
        await using var trees = Create(id => new Block(async context =>
        {
            context.ReportRunning();
            if (id == UiUids.WebViewHost) entered.TrySetResult();
            await Stopped(context.StopNotification);
            if (id == UiUids.WebViewHost)
            {
                stopBrowser.TrySetResult();
                await releaseBrowser.Task;
            }
            if (id == UiUids.LoopbackProxy) stopProxy.TrySetResult();
        }));
        try
        {
            Assert.Equal(UiUids.LoopbackProxy,
                trees.Read().Graph.Single(node => node.Id == UiUids.WebViewHost).ParentId);
            await trees.SetParentWantsRunningAsync(true);
            await trees.SetBackendSessionAsync(true, 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await trees.SetParentWantsRunningAsync(false);
            await stopBrowser.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(stopProxy.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Running, trees.ReadState(UiUids.LoopbackProxy));
            releaseBrowser.SetResult();
            await trees.WaitForStateAsync(UiUids.MainWindow, BusinessBlockState.Stopped)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(stopProxy.Task.IsCompleted);
        }
        finally { releaseBrowser.TrySetResult(); }
    }

    [Fact]
    public async Task WindowNodeUsesSessionFactAndProxyStateForFirstStartAndOfflineRetention()
    {
        var entered = Signal();
        var reportRunning = Signal();
        var stopBrowser = Signal();
        await using var trees = Create(id => new Block(async context =>
        {
            if (id == UiUids.LoopbackProxy)
            {
                entered.TrySetResult();
                await reportRunning.Task.WaitAsync(context.StopNotification);
            }
            context.ReportRunning();
            await Stopped(context.StopNotification);
            if (id == UiUids.WebViewHost) stopBrowser.TrySetResult();
        }));
        try
        {
            await trees.SetParentWantsRunningAsync(true);
            await trees.WaitForStateAsync(UiUids.MainWindow, BusinessBlockState.Running)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await trees.SetBackendSessionAsync(false, 0);
            Assert.Contains(trees.Read().CurrentDecisions, decision => decision.ParentId == UiUids.MainWindow);
            Assert.Equal(BusinessBlockState.Stopped, trees.ReadState(UiUids.LoopbackProxy));
            Assert.Equal(BusinessBlockState.Stopped, trees.ReadState(UiUids.WebViewHost));
            Assert.False(entered.Task.IsCompleted);

            await trees.SetBackendSessionAsync(true, 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(BusinessBlockState.Starting, trees.ReadState(UiUids.LoopbackProxy));
            await trees.SetBackendSessionAsync(false, 1);
            Assert.False(stopBrowser.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Starting, trees.ReadState(UiUids.LoopbackProxy));
            reportRunning.SetResult();
            await trees.WaitForStateAsync(UiUids.WebViewHost, BusinessBlockState.Running)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await trees.SetBackendSessionAsync(false, 2);
            Assert.Equal(BusinessBlockState.Running, trees.ReadState(UiUids.WebViewHost));
        }
        finally { reportRunning.TrySetResult(); }
    }

    private static NativeWindowBlockTrees Create(Func<int, IBusinessBlock> create)
        => new(create, NullLogger.Instance, NullLogger<BusinessBlockController>.Instance);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Stopped(CancellationToken token)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private sealed class Block(Func<BusinessBlockRunContext, Task> run) : IBusinessBlock
    {
        public Task RunAsync(BusinessBlockRunContext context) => run(context);
    }
}
