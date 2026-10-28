using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareCommandQueueTests
{
    [Fact]
    public async Task WaitingForAnUnstartedRunExpiresAndReleasesQueueCapacity()
    {
        using var queue = new HardwareCommandQueue(requestTimeout: TimeSpan.FromMilliseconds(50));
        var executed = false;
        await Assert.ThrowsAsync<TimeoutException>(() => queue.ExecuteAsync(_ =>
        {
            executed = true;
            return Task.FromResult(1);
        }, default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(executed);
        Assert.False(queue.HasPending);
    }

    [Fact]
    public async Task WaitingDeadlineDoesNotReleaseAnAlreadyAcceptedAction()
    {
        using var queue = new HardwareCommandQueue(requestTimeout: TimeSpan.FromMilliseconds(50));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = queue.ExecuteAsync(async _ => { entered.SetResult(); await release.Task; return 42; }, default);
        var drain = queue.DrainPendingAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.False(command.IsCompleted);
            Assert.True(queue.HasPending);
        }
        finally { release.TrySetResult(); }
        await drain;
        Assert.Equal(42, await command);
        Assert.False(queue.HasPending);
    }

    [Fact]
    public async Task CancelingMoreThanCapacityBeforeRunnerStartsLeavesCapacityAvailable()
    {
        var queue = new HardwareCommandQueue();
        var executed = 0;
        for (var index = 0; index < 256; index++)
        {
            using var cancellation = new CancellationTokenSource();
            var command = queue.ExecuteAsync(_ => Task.FromResult(++executed), cancellation.Token);
            Assert.True(queue.HasPending);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
            Assert.False(queue.HasPending);
        }
        Assert.Equal(0, executed);
        var accepted = queue.ExecuteAsync(_ => Task.FromResult(42), default);
        await queue.DrainPendingAsync(default);
        Assert.Equal(42, await accepted);
        Assert.False(queue.HasPending);
    }

    [Fact]
    public async Task CancelingActiveCallerKeepsPendingUntilActionActuallyFinishes()
    {
        var queue = new HardwareCommandQueue();
        using var caller = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = queue.ExecuteAsync(async active =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, active); return 1; }
            catch (OperationCanceledException)
            {
                canceled.SetResult();
                await release.Task;
                throw;
            }
        }, caller.Token);
        var runner = queue.RunAsync(stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.True(queue.HasPending);
            Assert.False(runner.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.False(queue.HasPending);
    }

    [Fact]
    public async Task DrainReturnsOriginalTypedResultAndExcludesCommandsAddedDuringDrain()
    {
        var queue = new HardwareCommandQueue();
        var original = new RecordedResult(42, "original");
        Task<int>? later = null;
        var first = queue.ExecuteAsync(_ =>
        {
            later = queue.ExecuteAsync(_ => Task.FromResult(99), default);
            return Task.FromResult(original);
        }, default);
        await queue.DrainPendingAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(original, await first);
        Assert.NotNull(later);
        Assert.False(later!.IsCompleted);
        Assert.True(queue.HasPending);
        await queue.DrainPendingAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(99, await later);
        Assert.False(queue.HasPending);
        await queue.DrainPendingAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AdmissionIsBoundedAndFailPendingReleasesAllWaitingCapacity()
    {
        var queue = new HardwareCommandQueue();
        var waiting = Enumerable.Range(0, 128).Select(value => queue.ExecuteAsync(_ => Task.FromResult(value), default)).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ExecuteAsync(_ => Task.FromResult(129), default));
        var failure = new IOException("Helper startup unavailable");
        queue.FailPending(failure);
        foreach (var command in waiting)
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => command));
        Assert.False(queue.HasPending);
        var accepted = queue.ExecuteAsync(_ => Task.FromResult(7), default);
        await queue.DrainPendingAsync(default);
        Assert.Equal(7, await accepted);
        Assert.False(queue.HasPending);
    }

    [Fact]
    public async Task RecoveredAvailabilityKeepsLateWaiterWithTheCommandRunner()
    {
        using var client = new ResourceManager.ReadWrite.Helpers.HardwareBridgeProcessClient(
            () => HardwareBridgeSessionTests.Fixture, HardwareBridgeSessionTests.Options);
        using var queue = new HardwareCommandQueue(bridge: client.Owner);
        var executions = 0;
        var pendingDuringExecution = false;
        var runnerToken = false;
        var command = queue.ExecuteAsync(token =>
        {
            Interlocked.Increment(ref executions);
            pendingDuringExecution = queue.HasPending;
            runnerToken = token.CanBeCanceled;
            return Task.FromResult(42);
        }, default);
        // Hold the admission lock until both notifications have occurred. The
        // waiter must observe recovery when it processes the old unavailable wake.
        var queueLock = typeof(HardwareCommandQueue).GetField("sync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(queue)!;
        lock (queueLock)
        {
            var installation = client.SuspendForInstallationAsync(default).GetAwaiter().GetResult();
            Assert.False(client.Owner.IsAvailable);
            installation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert.True(client.Owner.IsAvailable);
        }
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref executions));
        Assert.False(command.IsCompleted);
        Assert.True(queue.HasPending);
        using var stop = new CancellationTokenSource();
        var runner = queue.RunAsync(stop.Token);
        try
        {
            Assert.Equal(42, await command.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(pendingDuringExecution);
            Assert.True(runnerToken);
            Assert.Equal(1, executions);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.False(queue.HasPending);
    }

    private sealed record RecordedResult(int Value, string Label);
}
