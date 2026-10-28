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
        StateRegistryTests.AssertBlocks("ui",
            trees.Read().Blocks.Select(block => block.Id).Prepend(UiUids.Program));
        await trees.StartAsync(false);
        await probes.Next(UiUids.SingleInstance);
        await probes.Next(UiUids.BackendConnection);
        await probes.Next(UiUids.TrayRoot);
        Assert.Equal(BusinessBlockState.Stopped, trees.ReadState(UiUids.MainWindow));
        Assert.Equal(BusinessBlockState.Stopped, trees.ReadState(UiUids.WebViewHost));
        Assert.Equal(BusinessBlockState.Running, trees.ReadState(UiUids.MainRoot));
        Assert.Equal(BusinessBlockState.Running, trees.ReadState(UiUids.TrayRoot));

        await trees.SetBackendSessionReadyAsync(true, 1);
        await trees.SetWindowWantedAsync(true, "tray-click");
        await probes.Next(UiUids.WebViewHost);
        Assert.Equal(1, probes.Runs(UiUids.TrayRoot));
        Assert.Equal(1, probes.Runs(UiUids.BackendConnection));
        Assert.Equal(1, probes.Runs(UiUids.SingleInstance));
    }

    [Fact]
    public async Task TheRootDecidesWhichTreesRunFromItsDeclaredRule()
    {
        var probes = new Probes();
        await using var trees = Create(probes);
        Assert.All(trees.Read().Blocks, block => Assert.Equal(BusinessBlockState.Stopped, block.State));
        await trees.StartAsync(false);
        await probes.Next(UiUids.TrayRoot);
        var root = Assert.Single(trees.Read().CurrentDecisions, decision => decision.ParentId == UiUids.Program);
        Assert.Equal(new[] { UiUids.MainRoot, UiUids.TrayRoot }, root.ShouldRun);
        var started = Assert.Single(root.Items, item => item.ItemUid == UiItems.ProgramStarted);
        Assert.True(Assert.IsType<bool>(started.Value));
        Assert.Equal(new[] { UiItems.ProgramStarted, UiItems.ProgramShuttingDown }.Order(), root.Items.Select(item => item.ItemUid).Order());
        using var snapshot = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(trees.Read()));
        Assert.False(snapshot.RootElement.TryGetProperty("Demands", out _));
        Assert.False(snapshot.RootElement.TryGetProperty("demands", out _));
    }

    [Fact]
    public async Task ClosingWaitsForBrowserCleanupThenReopeningCreatesANewWindowBranch()
    {
        var cleanup = Signal();
        var probes = new Probes();
        probes.Cleanup(UiUids.WebViewHost, cleanup.Task);
        await using var trees = Create(probes);
        try
        {
            await trees.StartAsync(true);
            await trees.SetBackendSessionReadyAsync(true, 1);
            var window = await probes.Next(UiUids.MainWindow);
            var browser = await probes.Next(UiUids.WebViewHost);
            var proxy = await probes.Next(UiUids.LoopbackProxy);
            await probes.Next(UiUids.TrayRoot);
            await probes.Next(UiUids.SingleInstance);
            await probes.Next(UiUids.BackendConnection);

            await trees.SetWindowWantedAsync(false, "window-close");
            await browser.StopObserved.Task.WaitAsync(Timeout);
            Assert.False(proxy.StopObserved.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Running, trees.ReadState(UiUids.LoopbackProxy));
            Assert.False(window.StopObserved.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Running, trees.ReadState(UiUids.MainWindow));
            Assert.Equal(BusinessBlockState.Stopping, trees.ReadState(UiUids.WebViewHost));
            Assert.Equal(BusinessBlockState.Running, trees.ReadState(UiUids.TrayRoot));
            Assert.Equal(BusinessBlockState.Running, trees.ReadState(UiUids.BackendConnection));

            cleanup.TrySetResult();
            await trees.WaitForStateAsync(UiUids.MainWindow, BusinessBlockState.Stopped).WaitAsync(Timeout);
            Assert.Equal(BusinessBlockState.Stopped, trees.ReadState(UiUids.LoopbackProxy));
            Assert.Equal(BusinessBlockState.Stopped, trees.ReadState(UiUids.WebViewHost));

            await trees.SetWindowWantedAsync(true, "second-instance-show");
            var reopened = await probes.Next(UiUids.MainWindow);
            await probes.Next(UiUids.WebViewHost);
            await probes.Next(UiUids.LoopbackProxy);
            Assert.NotSame(window, reopened);
            Assert.Equal(2, probes.Runs(UiUids.MainWindow));
            Assert.Equal(1, probes.Runs(UiUids.TrayRoot));
            Assert.Equal(1, probes.Runs(UiUids.SingleInstance));
        }
        finally { cleanup.TrySetResult(); }
    }

    [Fact]
    public async Task ReturnedWindowCannotRestartUntilItsOldBrowserBranchDrains()
    {
        var cleanup = Signal();
        var probes = new Probes();
        probes.Cleanup(UiUids.WebViewHost, cleanup.Task);
        await using var trees = Create(probes);
        try
        {
            await trees.StartAsync(true);
            await trees.SetBackendSessionReadyAsync(true, 1);
            var window = await probes.Next(UiUids.MainWindow);
            var browser = await probes.Next(UiUids.WebViewHost);
            await probes.Next(UiUids.LoopbackProxy);
            window.Finish.TrySetResult();
            await trees.WaitForStateAsync(UiUids.MainWindow, BusinessBlockState.Stopped).WaitAsync(Timeout);
            await browser.StopObserved.Task.WaitAsync(Timeout);
            await trees.SetWindowWantedAsync(false, "window-close");
            await trees.SetWindowWantedAsync(true, "tray-click");
            Assert.Equal(1, probes.Runs(UiUids.MainWindow));
            Assert.Equal(BusinessBlockState.Stopping, trees.ReadState(UiUids.WebViewHost));

            cleanup.TrySetResult();
            var replacement = await probes.Next(UiUids.MainWindow);
            await probes.Next(UiUids.WebViewHost);
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
        var window = await probes.Next(UiUids.MainWindow);
        await trees.SetBackendSessionReadyAsync(true, 11);
        var browser = await probes.Next(UiUids.WebViewHost);
        var desktop = await probes.Next(UiUids.DesktopObservation);
        var overlay = await probes.Next(UiUids.Overlay);
        var notifications = await probes.Next(UiUids.TrayNotifications);

        await trees.SetBackendSessionReadyAsync(false, 11);
        await Task.WhenAll(desktop.Returned.Task, overlay.Returned.Task, notifications.Returned.Task).WaitAsync(Timeout);
        await Task.WhenAll(
            trees.WaitForStateAsync(UiUids.DesktopObservation, BusinessBlockState.Stopped),
            trees.WaitForStateAsync(UiUids.Overlay, BusinessBlockState.Stopped),
            trees.WaitForStateAsync(UiUids.TrayNotifications, BusinessBlockState.Stopped)).WaitAsync(Timeout);
        Assert.Equal(BusinessBlockState.Running, trees.ReadState(UiUids.MainWindow));
        Assert.Equal(BusinessBlockState.Running, trees.ReadState(UiUids.WebViewHost));
        Assert.False(window.StopObserved.Task.IsCompleted);
        Assert.False(browser.StopObserved.Task.IsCompleted);

        await trees.SetBackendSessionReadyAsync(true, 12);
        Assert.NotSame(desktop, await probes.Next(UiUids.DesktopObservation));
        Assert.NotSame(overlay, await probes.Next(UiUids.Overlay));
        Assert.NotSame(notifications, await probes.Next(UiUids.TrayNotifications));
        var session = trees.Read().Items.Single(fact => fact.ItemUid == UiItems.BackendSessionReady);
        Assert.True(Assert.IsType<bool>(session.Value));
        Assert.Contains(trees.Read().Decisions, decision => decision.Reason.PlanVersion == 12
            && decision.Items.Any(item => item.ItemUid == UiItems.BackendSessionReady && item.Value is true));
        Assert.Equal(1, probes.Runs(UiUids.MainWindow));
    }

    [Fact]
    public async Task TerminalShutdownStopsTheTreeAndLateFactsCannotRestartIt()
    {
        var probes = new Probes();
        await using var trees = Create(probes);
        await trees.StartAsync(true);
        await trees.SetBackendSessionReadyAsync(true, 1);
        await probes.Next(UiUids.WebViewHost);
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
        var canceled = trees.WaitForStateAsync(UiUids.MainWindow, BusinessBlockState.Running, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        await trees.StartAsync(true);
        await trees.WaitForStateAsync(UiUids.MainWindow, BusinessBlockState.Running).WaitAsync(Timeout);
    }

    private static NativeUiBlockTrees Create(Probes probes)
        => new(probes.Create, NullLogger.Instance, NullLogger<BusinessBlockController>.Instance);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Probes
    {
        private readonly ConcurrentDictionary<int, Channel<Execution>> runs = new();
        private readonly ConcurrentDictionary<int, int> counts = new();
        private readonly ConcurrentDictionary<int, Task> cleanup = new();
        internal int TotalRuns => counts.Values.Sum();
        internal int Runs(int id) => counts.GetValueOrDefault(id);
        internal void Cleanup(int id, Task release) => cleanup[id] = release;
        internal IBusinessBlock Create(int id) => new Block(this, id);
        internal async Task<Execution> Next(int id)
            => await GetChannel(id).Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        private Channel<Execution> GetChannel(int id) => runs.GetOrAdd(id, _ => Channel.CreateUnbounded<Execution>());

        private sealed class Block(Probes owner, int id) : IBusinessBlock
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
