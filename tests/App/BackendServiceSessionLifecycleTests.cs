using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class BackendServiceSessionLifecycleTests
{
    [Fact]
    public async Task DisposeAsyncWaitsForMonitorCleanupWithoutConnectingToAService()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new BackendServiceAvailabilityMonitor(_ => Task.FromResult(true), null,
            delayAsync: async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally
                {
                    canceled.TrySetResult();
                    await release.Task;
                }
            });
        monitor.Start();
        var session = new BackendServiceSession("http://127.0.0.1:1", availabilityMonitor: monitor);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var disposal = session.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(disposal.IsCompleted);
            session.Dispose();
            var repeated = session.DisposeAsync().AsTask();
            Assert.False(repeated.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(disposal, repeated).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(monitor.Completion.IsCompletedSuccessfully);
        }
        finally
        {
            release.TrySetResult();
            await session.DisposeAsync();
        }
    }
}
