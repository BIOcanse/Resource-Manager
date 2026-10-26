using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.NativeUi.Blocks;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiBlockTreeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task BackgroundStartupKeepsIndependentTrayAndMainServicesWithoutAWindow()
    {
        var probes = new Probes();
        await using var trees = Create(probes);
        await trees.StartAsync(false);
        await probes.Next(NativeUiBlockIds.SingleInstance);
        await probes.Next(NativeUiBlockIds.BackendConnection);
        await probes.Next(NativeUiBlockIds.TrayRoot);
        Assert.Equal(BusinessBlockState.Stopped, trees.ReadState(NativeUiBlockIds.MainWindow));
        Assert.Equal(BusinessBlockState.Stopped, trees.ReadState(NativeUiBlockIds.WebViewHost));
        Assert.Equal(BusinessBlockState.Running, trees.ReadState(NativeUiBlockIds.MainRoot));
        Assert.Equal(BusinessBlockState.Running, trees.ReadState(NativeUiBlockIds.TrayRoot));

        await trees.SetBackendSessionReadyAsync(true, 1);
        await trees.SetWindowWantedAsync(true, "tray-click");
        await probes.Next(NativeUiBlockIds.WebViewHost);
        Assert.Equal(1, probes.Runs(NativeUiBlockIds.TrayRoot));
        Assert.Equal(1, probes.Runs(NativeUiBlockIds.BackendConnection));
        Assert.Equal(1, probes.Runs(NativeUiBlockIds.SingleInstance));
    }

    [Fact]
    public async Task ClosingWaitsForBrowserCleanupThenReopeningCreatesANewWindowBranch()
    {
        var cleanup = Signal();
        var probes = new Probes();
        probes.Cleanup(NativeUiBlockIds.WebViewHost, cleanup.Task);
        await using var trees = Create(probes);
        try
        {
            await trees.StartAsync(true);
            await trees.SetBackendSessionReadyAsync(true, 1);
            var window = await probes.Next(NativeUiBlockIds.MainWindow);
            var browser = await probes.Next(NativeUiBlockIds.WebViewHost);
            var proxy = await probes.Next(NativeUiBlockIds.LoopbackProxy);
            await probes.Next(NativeUiBlockIds.TrayRoot);
            await probes.Next(NativeUiBlockIds.SingleInstance);
            await probes.Next(NativeUiBlockIds.BackendConnection);

            await trees.SetWindowWantedAsync(false, "window-close");
            await browser.StopObserved.Task.WaitAsync(Timeout);
            Assert.False(proxy.StopObserved.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Running, trees.ReadState(NativeUiBlockIds.LoopbackProxy));
            Assert.False(window.StopObserved.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Running, trees.ReadState(NativeUiBlockIds.MainWindow));
            Assert.Equal(BusinessBlockState.Stopping, trees.ReadState(NativeUiBlockIds.WebViewHost));
            Assert.Equal(BusinessBlockState.Running, trees.ReadState(NativeUiBlockIds.TrayRoot));
            Assert.Equal(BusinessBlockState.Running, trees.ReadState(NativeUiBlockIds.BackendConnection));

            cleanup.TrySetResult();
            await trees.WaitForStateAsync(NativeUiBlockIds.MainWindow, BusinessBlockState.Stopped).WaitAsync(Timeout);
            Assert.Equal(BusinessBlockState.Stopped, trees.ReadState(NativeUiBlockIds.LoopbackProxy));
            Assert.Equal(BusinessBlockState.Stopped, trees.ReadState(NativeUiBlockIds.WebViewHost));

            await trees.SetWindowWantedAsync(true, "second-instance-show");
            var reopened = await probes.Next(NativeUiBlockIds.MainWindow);
            await probes.Next(NativeUiBlockIds.WebViewHost);
            await probes.Next(NativeUiBlockIds.LoopbackProxy);
            Assert.NotSame(window, reopened);
            Assert.Equal(2, probes.Runs(NativeUiBlockIds.MainWindow));
            Assert.Equal(1, probes.Runs(NativeUiBlockIds.TrayRoot));
            Assert.Equal(1, probes.Runs(NativeUiBlockIds.SingleInstance));
        }
        finally { cleanup.TrySetResult(); }
    }

    [Fact]
    public async Task ReturnedWindowCannotRestartUntilItsOldBrowserBranchDrains()
    {
        var cleanup = Signal();
        var probes = new Probes();
        probes.Cleanup(NativeUiBlockIds.WebViewHost, cleanup.Task);
        await using var trees = Create(probes);
        try
        {
            await trees.StartAsync(true);
            await trees.SetBackendSessionReadyAsync(true, 1);
            var window = await probes.Next(NativeUiBlockIds.MainWindow);
            var browser = await probes.Next(NativeUiBlockIds.WebViewHost);
            await probes.Next(NativeUiBlockIds.LoopbackProxy);
            window.Finish.TrySetResult();
            await trees.WaitForStateAsync(NativeUiBlockIds.MainWindow, BusinessBlockState.Stopped).WaitAsync(Timeout);
            await browser.StopObserved.Task.WaitAsync(Timeout);
            await trees.SetWindowWantedAsync(false, "window-close");
            await trees.SetWindowWantedAsync(true, "tray-click");
            Assert.Equal(1, probes.Runs(NativeUiBlockIds.MainWindow));
            Assert.Equal(BusinessBlockState.Stopping, trees.ReadState(NativeUiBlockIds.WebViewHost));

            cleanup.TrySetResult();
            var replacement = await probes.Next(NativeUiBlockIds.MainWindow);
            await probes.Next(NativeUiBlockIds.WebViewHost);
            Assert.NotSame(window, replacement);
            Assert.True(browser.Returned.Task.IsCompleted);
        }
        finally { cleanup.TrySetResult(); }
    }

    [Fact]
    public async Task SessionLossStopsOnlyConnectionChildrenAndTrayNotificationsThenFreshSessionRestartsThem()
    {
        var probes = new Probes();
        await using var trees = Create(probes);
        await trees.StartAsync(true);
        var window = await probes.Next(NativeUiBlockIds.MainWindow);
        await trees.SetBackendSessionReadyAsync(true, 11);
        var browser = await probes.Next(NativeUiBlockIds.WebViewHost);
        var desktop = await probes.Next(NativeUiBlockIds.DesktopObservation);
        var overlay = await probes.Next(NativeUiBlockIds.Overlay);
        var notifications = await probes.Next(NativeUiBlockIds.TrayNotifications);

        await trees.SetBackendSessionReadyAsync(false, 11);
        await Task.WhenAll(desktop.Returned.Task, overlay.Returned.Task, notifications.Returned.Task).WaitAsync(Timeout);
        await Task.WhenAll(
            trees.WaitForStateAsync(NativeUiBlockIds.DesktopObservation, BusinessBlockState.Stopped),
            trees.WaitForStateAsync(NativeUiBlockIds.Overlay, BusinessBlockState.Stopped),
            trees.WaitForStateAsync(NativeUiBlockIds.TrayNotifications, BusinessBlockState.Stopped)).WaitAsync(Timeout);
        Assert.Equal(BusinessBlockState.Running, trees.ReadState(NativeUiBlockIds.MainWindow));
        Assert.Equal(BusinessBlockState.Running, trees.ReadState(NativeUiBlockIds.WebViewHost));
        Assert.False(window.StopObserved.Task.IsCompleted);
        Assert.False(browser.StopObserved.Task.IsCompleted);

        await trees.SetBackendSessionReadyAsync(true, 12);
        Assert.NotSame(desktop, await probes.Next(NativeUiBlockIds.DesktopObservation));
        Assert.NotSame(overlay, await probes.Next(NativeUiBlockIds.Overlay));
        Assert.NotSame(notifications, await probes.Next(NativeUiBlockIds.TrayNotifications));
        var session = trees.Read().Facts.Single(fact => fact.Key == NativeUiBlockTrees.BackendSessionReadyFact);
        Assert.True(session.Value);
        Assert.Equal(12, Assert.Single(session.Reasons).PlanVersion);
        Assert.Equal(1, probes.Runs(NativeUiBlockIds.MainWindow));
    }

    [Fact]
    public async Task TerminalShutdownStopsTheTreeAndLateFactsCannotRestartIt()
    {
        var probes = new Probes();
        await using var trees = Create(probes);
        await trees.StartAsync(true);
        await trees.SetBackendSessionReadyAsync(true, 1);
        await probes.Next(NativeUiBlockIds.WebViewHost);
        await trees.ShutdownAsync(new(BusinessBlockRequestKind.User, "program", "exit", 1));
        Assert.All(trees.Read().Blocks, block => Assert.Equal(BusinessBlockState.Stopped, block.State));
        var starts = probes.TotalRuns;
        await trees.SetWindowWantedAsync(true, "late-show");
        await trees.SetBackendSessionReadyAsync(true, 2);
        Assert.Equal(starts, probes.TotalRuns);
        Assert.All(trees.Read().Blocks, block => Assert.Equal(BusinessBlockState.Stopped, block.State));
    }

    [Fact]
    public async Task StateWaitCancellationDoesNotAffectSubsequentStateWaits()
    {
        var probes = new Probes();
        await using var trees = Create(probes);
        using var cancellation = new CancellationTokenSource();
        var canceled = trees.WaitForStateAsync(NativeUiBlockIds.MainWindow, BusinessBlockState.Running, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        await trees.StartAsync(true);
        await trees.WaitForStateAsync(NativeUiBlockIds.MainWindow, BusinessBlockState.Running).WaitAsync(Timeout);
    }

    private static NativeUiBlockTrees Create(Probes probes)
        => new(probes.Create, NullLogger.Instance, NullLogger<BusinessBlockController>.Instance);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Probes
    {
        private readonly ConcurrentDictionary<string, Channel<Execution>> runs = new();
        private readonly ConcurrentDictionary<string, int> counts = new();
        private readonly ConcurrentDictionary<string, Task> cleanup = new();
        internal int TotalRuns => counts.Values.Sum();
        internal int Runs(string id) => counts.GetValueOrDefault(id);
        internal void Cleanup(string id, Task release) => cleanup[id] = release;
        internal IBusinessBlock Create(string id) => new Block(this, id);
        internal async Task<Execution> Next(string id)
            => await GetChannel(id).Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        private Channel<Execution> GetChannel(string id) => runs.GetOrAdd(id, _ => Channel.CreateUnbounded<Execution>());

        private sealed class Block(Probes owner, string id) : IBusinessBlock
        {
            public async Task RunAsync(BusinessBlockRunContext context)
            {
                var execution = new Execution();
                owner.counts.AddOrUpdate(id, 1, (_, previous) => previous + 1);
                context.ReportRunning();
                using var registration = context.StopNotification.Register(() => execution.StopObserved.TrySetResult());
                owner.GetChannel(id).Writer.TryWrite(execution);
                await Task.WhenAny(execution.StopObserved.Task, execution.Finish.Task);
                if (owner.cleanup.TryGetValue(id, out var cleanup)) await cleanup;
                execution.Returned.TrySetResult();
            }
        }
    }

    private sealed class Execution
    {
        internal TaskCompletionSource StopObserved { get; } = Signal();
        internal TaskCompletionSource Finish { get; } = Signal();
        internal TaskCompletionSource Returned { get; } = Signal();
    }
}
