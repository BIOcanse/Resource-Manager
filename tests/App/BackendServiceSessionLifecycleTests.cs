using ResourceManager.NativeUi;
using System.Net.Http;

namespace Resource_Manager_APP.Tests;

public sealed class BackendServiceSessionLifecycleTests
{
    [Fact]
    public async Task OwnerReleaseKeepsAllClientsAliveUntilTheLastConsumerReturnsItsHold()
    {
        var handlers = new List<DisposalTrackingHandler>();
        var session = new BackendServiceSession("http://127.0.0.1:1", handlerFactory: () =>
        {
            var handler = new DisposalTrackingHandler();
            handlers.Add(handler);
            return handler;
        });
        using var first = Assert.IsType<BackendServiceSession.Hold>(session.TryAcquire());
        using var last = Assert.IsType<BackendServiceSession.Hold>(session.TryAcquire());
        try
        {
            Assert.Same(session, first.Session);
            Assert.Equal(5, handlers.Count);
            await session.DisposeAsync();
            Assert.Null(session.TryAcquire());
            Assert.All(handlers, handler => Assert.Equal(0, handler.DisposeCount));
            first.Dispose();
            first.Dispose();
            session.Dispose();
            Assert.All(handlers, handler => Assert.Equal(0, handler.DisposeCount));
            last.Dispose();
            Assert.All(handlers, handler => Assert.Equal(1, handler.DisposeCount));
            last.Dispose();
            await session.DisposeAsync();
            Assert.All(handlers, handler => Assert.Equal(1, handler.DisposeCount));
        }
        finally { await session.DisposeAsync(); }
    }

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

    private sealed class DisposalTrackingHandler : HttpMessageHandler
    {
        internal int DisposeCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No network request is expected.");
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
