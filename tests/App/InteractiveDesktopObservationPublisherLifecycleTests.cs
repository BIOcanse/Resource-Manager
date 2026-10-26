using ResourceManager.NativeUi.SystemIntegration;

namespace Resource_Manager_APP.Tests;

public sealed class InteractiveDesktopObservationPublisherLifecycleTests
{
    [Fact]
    public async Task StopWaitsForPublicationCleanupAndRestartDoesNotOverlap()
    {
        await using var publisher = new InteractiveDesktopObservationPublisher();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpectedStarts = 0;
        var first = publisher.RunAsync(async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                canceled.TrySetResult();
                await release.Task;
            }
        }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var stopping = publisher.StopAsync();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(stopping.IsCompleted);
            Assert.Same(first, publisher.RunAsync(_ =>
            {
                Interlocked.Increment(ref unexpectedStarts);
                return Task.CompletedTask;
            }, CancellationToken.None));
            Assert.Equal(0, unexpectedStarts);
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));

            var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource();
            var second = publisher.RunAsync(token =>
            {
                restarted.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, stop.Token);
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotSame(first, second);
            stop.Cancel();
            await second.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.TrySetResult();
            await publisher.StopAsync();
        }
    }

    [Fact]
    public async Task DisposedPublisherCannotStartAnotherFlow()
    {
        var publisher = new InteractiveDesktopObservationPublisher();
        await publisher.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = publisher.RunAsync(_ => Task.CompletedTask, CancellationToken.None);
        });
    }
}
