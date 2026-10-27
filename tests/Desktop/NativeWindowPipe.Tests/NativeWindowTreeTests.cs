using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.NativeUi.Blocks;
using ResourceManager.NativeUi.StateItems;
using ResourceManager.Shared.BusinessBlocks;

namespace ResourceManager.NativeWindow.Tests;

public sealed class NativeWindowTreeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OnlyParentDemandCreatesWindowAndReadyCreatesProxyBrowser()
    {
        var runs = new Runs();
        await using var tree = Create(runs);
        Assert.All(tree.Read().Blocks, block => Assert.Equal(BusinessBlockState.Stopped, block.State));
        Assert.Equal(new[] { BlockUids.MainWindow, BlockUids.LoopbackProxy, BlockUids.WebViewHost }.Order(),
            tree.Read().Blocks.Select(block => block.Id).Order());
        await tree.SetParentWantsRunningAsync(true);
        await runs.Next(BlockUids.MainWindow);
        Assert.Equal(BusinessBlockState.Stopped, tree.ReadState(BlockUids.LoopbackProxy));
        await tree.SetBackendSessionAsync(true, 7);
        await runs.Next(BlockUids.LoopbackProxy);
        await runs.Next(BlockUids.WebViewHost);
        await tree.SetBackendSessionAsync(false, 7);
        Assert.Equal(BusinessBlockState.Running, tree.ReadState(BlockUids.LoopbackProxy));
        Assert.Equal(BusinessBlockState.Running, tree.ReadState(BlockUids.WebViewHost));
        await tree.SetBackendSessionAsync(true, 8);
        Assert.All(runs.Counts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task ParentStopDrainsBrowserThenProxyThenWindow()
    {
        var runs = new Runs();
        var release = Signal();
        runs.BrowserCleanup = release.Task;
        await using var tree = Create(runs);
        try
        {
            await tree.SetParentWantsRunningAsync(true);
            var window = await runs.Next(BlockUids.MainWindow);
            await tree.SetBackendSessionAsync(true, 1);
            var proxy = await runs.Next(BlockUids.LoopbackProxy);
            var browser = await runs.Next(BlockUids.WebViewHost);
            await tree.SetParentWantsRunningAsync(false);
            await browser.Stopping.Task.WaitAsync(Limit);
            Assert.False(proxy.Stopping.Task.IsCompleted);
            Assert.False(window.Stopping.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Stopping, tree.ReadState(BlockUids.WebViewHost));
            release.SetResult();
            await tree.WaitForStateAsync(BlockUids.MainWindow, BusinessBlockState.Stopped).WaitAsync(Limit);
            Assert.All(tree.Read().Blocks, block => Assert.Equal(BusinessBlockState.Stopped, block.State));
        }
        finally { release.TrySetResult(); }
    }

    private static NativeWindowBlockTrees Create(Runs runs)
        => new(id => new Probe(id, runs), NullLogger.Instance, NullLogger<BusinessBlockController>.Instance);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Runs
    {
        private readonly ConcurrentDictionary<int, Channel<Probe>> channels = new();
        internal readonly ConcurrentDictionary<int, int> Counts = new();
        internal Task BrowserCleanup = Task.CompletedTask;
        internal void Add(int id, Probe probe)
        {
            Counts.AddOrUpdate(id, 1, (_, count) => count + 1);
            channels.GetOrAdd(id, _ => Channel.CreateUnbounded<Probe>()).Writer.TryWrite(probe);
        }
        internal async Task<Probe> Next(int id)
            => await channels.GetOrAdd(id, _ => Channel.CreateUnbounded<Probe>()).Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class Probe(int id, Runs runs) : IBusinessBlock
    {
        internal TaskCompletionSource Stopping = Signal();
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            context.ReportRunning(); runs.Add(id, this);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
            Stopping.SetResult();
            if (id == BlockUids.WebViewHost) await runs.BrowserCleanup;
        }
    }
}
