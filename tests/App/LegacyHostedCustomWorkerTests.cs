using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed class LegacyHostedCustomWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WorkerFaultCancelsAndJoinsSiblingBeforePublishingOriginalFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var failing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingStopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSibling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sibling = RunSiblingAsync();
        var lifetime = JoinOwnedWorkers(cancellation, failing.Task, sibling);
        var original = new InvalidDataException("injected worker failure");
        failing.SetException(original);

        await siblingStopping.Task.WaitAsync(Timeout);
        Assert.False(lifetime.IsCompleted);
        releaseSibling.SetResult();
        Assert.Same(original, await Assert.ThrowsAsync<InvalidDataException>(
            () => lifetime.WaitAsync(Timeout)));
        Assert.True(sibling.IsCompletedSuccessfully);

        async Task RunSiblingAsync()
        {
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            siblingStopping.SetResult();
            await releaseSibling.Task;
        }
    }

    [Fact]
    public async Task UnexpectedSuccessfulWorkerReturnCancelsWaitingSibling()
    {
        using var cancellation = new CancellationTokenSource();
        var sibling = Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellation.Token);
        var lifetime = JoinOwnedWorkers(cancellation, Task.CompletedTask, sibling);

        await lifetime.WaitAsync(Timeout);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(sibling.IsCompleted);
    }

    [Fact]
    public async Task SiblingCancellationBeforeFaultPublicationDoesNotHideOriginalFault()
    {
        using var cancellation = new CancellationTokenSource();
        var original = new InvalidDataException("injected late fault publication");
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sibling = Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellation.Token);
        await cancellation.CancelAsync();
        var lifetime = JoinOwnedWorkers(cancellation, sibling, fault.Task);
        fault.SetException(original);

        Assert.Same(original, await Assert.ThrowsAsync<InvalidDataException>(
            () => lifetime.WaitAsync(Timeout)));
    }

    [Fact]
    public async Task CancellationCallbackFailureDoesNotReplaceWorkerFailureOrSkipSiblingJoin()
    {
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(
            () => throw new InvalidOperationException("injected cancellation callback failure"));
        var original = new InvalidDataException("injected primary worker failure");
        var sibling = Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellation.Token);
        var lifetime = JoinOwnedWorkers(cancellation, Task.FromException(original), sibling);

        Assert.Same(original, await Assert.ThrowsAsync<InvalidDataException>(
            () => lifetime.WaitAsync(Timeout)));
        Assert.True(sibling.IsCompleted);
    }

    [Fact]
    public async Task TrustedAccessWorkerFaultIsObservableAndStopAllowsFreshRun()
    {
        var clock = new FaultingClock();
        var owner = new TrustedAccessRequestOwner(null!, null!, clock, NotificationTestSupport.Create());
        await owner.StartAsync(CancellationToken.None);
        var first = Assert.IsAssignableFrom<Task>(owner.ExecutionTask);
        Assert.Same(clock.Failure, await Assert.ThrowsAsync<InvalidDataException>(
            () => first.WaitAsync(Timeout)));
        await owner.StopAsync(CancellationToken.None);
        Assert.Same(first, owner.ExecutionTask);
        Assert.Same(clock.Failure, await Assert.ThrowsAsync<InvalidDataException>(
            () => first.WaitAsync(Timeout)));

        clock.Throw = false;
        await owner.StartAsync(CancellationToken.None);
        var second = Assert.IsAssignableFrom<Task>(owner.ExecutionTask);
        Assert.NotSame(first, second);
        Assert.False(second.IsCompleted);
        await owner.StopAsync(CancellationToken.None);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Same(second, owner.ExecutionTask);
    }

    private static Task JoinOwnedWorkers(CancellationTokenSource cancellation, params Task[] workers)
    {
        var firstWorker = Task.WhenAny(workers);
        var ownerDrain = Task.Run(async () =>
        {
            await firstWorker.ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
        });
        return HostedWorkerLifetime.JoinAsync(firstWorker, ownerDrain, cancellation.Token, workers);
    }

    private sealed class FaultingClock : TimeProvider
    {
        internal readonly InvalidDataException Failure = new("injected expiration clock failure");
        internal bool Throw = true;

        public override DateTimeOffset GetUtcNow()
            => Throw ? throw Failure : DateTimeOffset.UnixEpoch;
    }
}
