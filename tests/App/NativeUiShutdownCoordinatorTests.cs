using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiShutdownCoordinatorTests
{
    [Fact]
    public async Task ShutdownWaitsForBothTreesBeforeExitingAndRepeatedRequestsShareCompletion()
    {
        var drain = Signal();
        var calls = new List<string>();
        var coordinator = new NativeUiShutdownCoordinator();
        var actions = new NativeUiShutdownActions(
            MarkExiting: () => calls.Add("mark-exiting"),
            StopTrees: async () => { calls.Add("stop-trees"); await drain.Task; calls.Add("trees-stopped"); },
            ExitMessageLoop: () => calls.Add("exit-loop"));

        Assert.Null(coordinator.Completion);
        var first = coordinator.ShutdownAsync(actions);
        var second = coordinator.ShutdownAsync(actions);
        Assert.Same(first, second);
        Assert.Same(first, coordinator.Completion);
        Assert.False(first.IsCompleted);
        Assert.Equal(["mark-exiting", "stop-trees"], calls);

        drain.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["mark-exiting", "stop-trees", "trees-stopped", "exit-loop"], calls);
        Assert.Same(first, coordinator.ShutdownAsync(actions));
        Assert.Equal(4, calls.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReentrantMarkOrStopObservesPublishedCompletionWithoutRepeatingActions(bool reenterFromMark)
    {
        var drain = Signal();
        var coordinator = new NativeUiShutdownCoordinator();
        NativeUiShutdownActions? actions = null;
        Task? reentered = null;
        var marks = 0;
        var stops = 0;
        var exits = 0;
        actions = new(
            MarkExiting: () =>
            {
                marks++;
                if (reenterFromMark) reentered = coordinator.ShutdownAsync(actions!);
            },
            StopTrees: () =>
            {
                stops++;
                if (!reenterFromMark) reentered = coordinator.ShutdownAsync(actions!);
                return drain.Task;
            },
            ExitMessageLoop: () => exits++);

        var completion = coordinator.ShutdownAsync(actions);
        Assert.Same(completion, reentered);
        Assert.Same(completion, coordinator.Completion);
        Assert.Equal(1, marks);
        Assert.Equal(1, stops);
        Assert.Equal(0, exits);
        drain.TrySetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task FailedDrainFaultsSharedCompletionAndNeverExitsTheMessageLoop()
    {
        var drain = Signal();
        var coordinator = new NativeUiShutdownCoordinator();
        var exits = 0;
        var stops = 0;
        var failure = new InvalidOperationException("browser cleanup failed");
        var actions = new NativeUiShutdownActions(
            MarkExiting: () => { },
            StopTrees: () => { stops++; return drain.Task; },
            ExitMessageLoop: () => exits++);
        var completion = coordinator.ShutdownAsync(actions);
        drain.TrySetException(failure);

        var reported = await Assert.ThrowsAsync<InvalidOperationException>(() => completion);
        Assert.Same(failure, reported);
        Assert.Same(completion, coordinator.ShutdownAsync(actions));
        Assert.Equal(1, stops);
        Assert.Equal(0, exits);
    }

    [Fact]
    public async Task CanceledDrainDoesNotExitTheMessageLoop()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var exits = 0;
        var coordinator = new NativeUiShutdownCoordinator();
        var completion = coordinator.ShutdownAsync(new(
            () => { }, () => Task.FromCanceled(canceled.Token), () => exits++));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.True(completion.IsCanceled);
        Assert.Equal(0, exits);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
