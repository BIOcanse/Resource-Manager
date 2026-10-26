using System.Collections.Concurrent;
using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiCallbackLifetimeTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task StopRetiresQueuedWorkAndRejectsAnEventCapturedBeforeUnsubscription()
    {
        var queue = new Queue<(Action Action, TaskCompletionSource Completion)>();
        var calls = 0;
        var dispatches = 0;
        await using var lifetime = new NativeUiCallbackLifetime(action =>
        {
            dispatches++;
            var completion = Signal();
            queue.Enqueue((action, completion));
            return completion.Task;
        });
        Action lateEvent = () => lifetime.Post(() => calls++);
        lifetime.Post(() => calls++);
        var stopping = lifetime.StopAsync();
        Assert.False(stopping.IsCompleted);
        lateEvent();
        Assert.Equal(1, dispatches);
        var queued = queue.Dequeue();
        queued.Action();
        Assert.Equal(0, calls);
        queued.Completion.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        lifetime.Post(() => calls++);
        Assert.Equal(1, dispatches);
        Assert.Same(stopping, lifetime.StopAsync());
    }

    [Fact]
    public async Task StopWaitsForAStartedCallbackAndItsDispatchCleanup()
    {
        var entered = Signal();
        var releaseAction = Signal();
        var cleanupEntered = Signal();
        var releaseCleanup = Signal();
        var returned = false;
        await using var lifetime = new NativeUiCallbackLifetime(async action =>
        {
            await Task.Run(action);
            cleanupEntered.TrySetResult();
            await releaseCleanup.Task;
        });
        lifetime.Post(() =>
        {
            entered.TrySetResult();
            releaseAction.Task.GetAwaiter().GetResult();
            returned = true;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var stopping = lifetime.StopAsync();
            Assert.False(stopping.IsCompleted);
            releaseAction.TrySetResult();
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(returned);
            Assert.False(stopping.IsCompleted);
            releaseCleanup.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseAction.TrySetResult();
            releaseCleanup.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchAndCallbackErrorsAreObservedWithoutPreventingStop(bool callbackFault)
    {
        var errors = new ConcurrentQueue<Exception>();
        var expected = new IOException("Dispatch failed.");
        await using var lifetime = new NativeUiCallbackLifetime(action =>
        {
            if (callbackFault) action();
            return Task.FromException(expected);
        }, errors.Enqueue);
        lifetime.Post(() => throw expected);
        await lifetime.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(expected, Assert.Single(errors));
    }
}
