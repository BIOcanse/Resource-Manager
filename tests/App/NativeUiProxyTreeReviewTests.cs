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
            if (id == NativeUiBlockIds.WebViewHost) entered.TrySetResult();
            await Stopped(context.StopNotification);
            if (id == NativeUiBlockIds.WebViewHost)
            {
                stopBrowser.TrySetResult();
                await releaseBrowser.Task;
            }
            if (id == NativeUiBlockIds.LoopbackProxy) stopProxy.TrySetResult();
        }));
        try
        {
            Assert.Equal(NativeUiBlockIds.LoopbackProxy,
                trees.Read().Graph.Single(node => node.Id == NativeUiBlockIds.WebViewHost).ParentId);
            await trees.StartAsync(true);
            await trees.SetBackendSessionReadyAsync(true, 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await trees.SetWindowWantedAsync(false, "close");
            await stopBrowser.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(stopProxy.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Running, trees.ReadState(NativeUiBlockIds.LoopbackProxy));
            releaseBrowser.SetResult();
            await trees.WaitForStateAsync(NativeUiBlockIds.MainWindow, BusinessBlockState.Stopped)
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
            if (id == NativeUiBlockIds.LoopbackProxy)
            {
                entered.TrySetResult();
                await reportRunning.Task.WaitAsync(context.StopNotification);
            }
            context.ReportRunning();
            await Stopped(context.StopNotification);
            if (id == NativeUiBlockIds.WebViewHost) stopBrowser.TrySetResult();
        }));
        try
        {
            await trees.StartAsync(true);
            await trees.WaitForStateAsync(NativeUiBlockIds.MainWindow, BusinessBlockState.Running)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await trees.SetBackendSessionReadyAsync(false, 0);
            Assert.Contains(trees.Read().CurrentDecisions, decision => decision.ParentId == NativeUiBlockIds.MainWindow);
            Assert.Equal(BusinessBlockState.Stopped, trees.ReadState(NativeUiBlockIds.LoopbackProxy));
            Assert.Equal(BusinessBlockState.Stopped, trees.ReadState(NativeUiBlockIds.WebViewHost));
            Assert.False(entered.Task.IsCompleted);

            await trees.SetBackendSessionReadyAsync(true, 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(BusinessBlockState.Starting, trees.ReadState(NativeUiBlockIds.LoopbackProxy));
            await trees.SetBackendSessionReadyAsync(false, 1);
            Assert.False(stopBrowser.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Starting, trees.ReadState(NativeUiBlockIds.LoopbackProxy));
            reportRunning.SetResult();
            await trees.WaitForStateAsync(NativeUiBlockIds.WebViewHost, BusinessBlockState.Running)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await trees.SetBackendSessionReadyAsync(false, 2);
            Assert.Equal(BusinessBlockState.Running, trees.ReadState(NativeUiBlockIds.WebViewHost));
        }
        finally { reportRunning.TrySetResult(); }
    }

    private static NativeUiBlockTrees Create(Func<string, IBusinessBlock> create)
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
