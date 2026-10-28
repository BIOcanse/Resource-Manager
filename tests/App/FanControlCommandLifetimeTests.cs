using System.Text.Json;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class FanControlCommandLifetimeTests
{
    private static JsonElement Description => JsonSerializer.SerializeToElement(new {
        ok = true, available = true, backend = "fixture", fans = new[] { new { index = 0, writable = true } }
    });

    [Fact]
    public async Task CallerCancellationCannotDisposeItsLinkBeforeCancellingTheQueuedCommand()
    {
        // Run cancellation synchronously without the test runner's synchronization
        // context, so the outer waiter can resume inside the LIFO callback stack.
        await Task.Run(async () =>
        {
            var opened = 0;
            await using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
            {
                Interlocked.Increment(ref opened);
                return new DelegateFanSession(_ => Description);
            });
            var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var edges = 0;
            core.PendingCommandsChanged += () =>
            {
                Interlocked.Increment(ref edges);
                if (!core.PendingCommands) cleared.TrySetResult();
            };
            using var caller = new CancellationTokenSource();
            var tailCallbackReached = false;
            // Registered first, this observes the end of the same Cancel callback
            // stack after the outer waiter and the linked source had their turns.
            using var tail = caller.Token.Register(() => tailCallbackReached = true);
            var queued = core.SendAsync("read", null, caller.Token);
            Assert.True(core.PendingCommands);
            caller.Cancel();
            Assert.True(tailCallbackReached);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(core.PendingCommands);
            Assert.Equal(2, Volatile.Read(ref edges));
            Assert.Equal(0, opened);
        });
    }

    [Fact]
    public async Task QueuedCommandsPublishOnlyZeroEdgesAndNeverOpenTheirOwnSession()
    {
        var opened = 0;
        await using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            Interlocked.Increment(ref opened);
            return new DelegateFanSession(_ => Description);
        });
        var edges = new List<bool>();
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.PendingCommandsChanged += () =>
        {
            edges.Add(core.PendingCommands);
            if (!core.PendingCommands) cleared.TrySetResult();
        };
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = core.SendAsync("read", null, firstCancellation.Token);
        var second = core.SendAsync("read", null, secondCancellation.Token);
        Assert.True(core.PendingCommands);
        Assert.Equal([true], edges);
        Assert.Equal(0, opened);
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(core.PendingCommands);
        Assert.Equal([true], edges);
        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(core.PendingCommands);
        Assert.Equal([true, false], edges);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task PendingIncludesGateWaitAndAcceptedWorkUntilActualCompletion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => new DelegateFanSession(request =>
        {
            if (request.GetProperty("op").GetString() == "set")
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            return Description;
        }));
        await core.StartSession();
        var edges = new List<bool>();
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.PendingCommandsChanged += () =>
        {
            edges.Add(core.PendingCommands);
            if (!core.PendingCommands) cleared.TrySetResult();
        };
        using var acceptedCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        var accepted = core.SendAsync("set", null, acceptedCancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = core.SendAsync("read", null, queuedCancellation.Token);
            acceptedCancellation.Cancel();
            queuedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepted.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(core.PendingCommands);
            Assert.Equal([true], edges);
        }
        finally { release.Set(); }
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(core.PendingCommands);
        Assert.Equal([true, false], edges);
    }

    [Fact]
    public async Task FiniteDiscoveryOpensAndClosesOnceWhilePassiveReadsOnlyUseCache()
    {
        var opened = 0;
        var closed = 0;
        var described = 0;
        await using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            Interlocked.Increment(ref opened);
            return new DelegateFanSession(_ => { Interlocked.Increment(ref described); return Description; },
                () => Interlocked.Increment(ref closed));
        });
        Assert.Empty(core.Describe());
        Assert.Null(core.UnavailableReason);
        Assert.Equal(0, opened);
        Assert.Single(await core.DiscoverAsync(default));
        Assert.Equal(1, opened);
        Assert.Equal(1, closed);
        Assert.Single(core.Describe());
        Assert.Single(await core.DiscoverAsync(default));
        Assert.Equal(1, described);
        Assert.False(core.PendingCommands);
        core.ResetDetection();
        Assert.Empty(core.Describe());
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task DiscoveryReusesRunHeldSessionAndDoesNotReleaseIt()
    {
        var opened = 0;
        var closed = 0;
        await using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            Interlocked.Increment(ref opened);
            return new DelegateFanSession(_ => Description, () => Interlocked.Increment(ref closed));
        });
        await core.StartSession();
        core.ResetDetection();
        Assert.Single(await core.DiscoverAsync(default));
        Assert.Equal(1, opened);
        Assert.Equal(0, closed);
        await core.StopSessionAsync();
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task RunJoiningFiniteDiscoveryKeepsTheFirstSessionUntilItsLastHolderLeaves()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var opened = 0;
        var closed = 0;
        await using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            Interlocked.Increment(ref opened);
            return new DelegateFanSession(_ =>
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return Description;
            }, () => Interlocked.Increment(ref closed));
        });
        var discovery = core.DiscoverAsync(default);
        Task? startup = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            startup = core.StartSession();
            Assert.False(startup.IsCompleted);
            Assert.Equal(1, opened);
        }
        finally { release.Set(); }
        Assert.Single(await discovery);
        await startup!;
        Assert.Equal(1, opened);
        Assert.Equal(0, closed);
        await core.StopSessionAsync();
        Assert.Equal(1, closed);
    }
}
