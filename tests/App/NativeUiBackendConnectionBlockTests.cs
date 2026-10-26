using System.Net.Http;
using System.Threading.Channels;
using ResourceManager.NativeUi;
using ResourceManager.Shared.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiBackendConnectionBlockTests
{
    [Fact]
    public async Task StartupFailuresKeepExistingBackoffAndManualRetryOnlyWakesTheCurrentDelay()
    {
        var delays = Channel.CreateUnbounded<TimeSpan>();
        var states = new List<BackendConnectionState>();
        var created = new List<BackendServiceSession>();
        var ready = Signal();
        var attempts = 0;
        var reports = 0;
        var block = new NativeUiBackendConnectionBlock(() =>
        {
            var session = NewSession();
            created.Add(session);
            return session;
        }, (state, session, generation, unavailable) =>
        {
            states.Add(state);
            Assert.Equal(state == BackendConnectionState.Ready, session is not null);
            if (state == BackendConnectionState.Degraded)
                Assert.Equal(BackendServiceUnavailableReason.StartupFailed, unavailable!.Reason);
            if (state == BackendConnectionState.Ready) ready.TrySetResult();
            return Task.CompletedTask;
        }, connectAsync: (_, _) => ++attempts < 3
            ? Task.FromException(new IOException("Connection attempt failed.")) : Task.CompletedTask,
            delayAsync: (delay, token) =>
            {
                delays.Writer.TryWrite(delay);
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        using var stop = new CancellationTokenSource();
        var run = block.RunAsync(new(stop.Token, () => reports++));
        try
        {
            Assert.Equal(BackendReconnectPolicy.GetDelay(0),
                await delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            block.RequestRetry();
            Assert.Equal(BackendReconnectPolicy.GetDelay(1),
                await delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            block.RequestRetry();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(block.IsReady);
            Assert.Equal(3, block.Generation);
            Assert.Equal(3, attempts);
            Assert.Equal(1, reports);
            Assert.Same(created[2], block.CurrentSession);
            block.RequestRetry();
            Assert.Equal(3, attempts);
            Assert.Equal(new[]
            {
                BackendConnectionState.Connecting, BackendConnectionState.Degraded,
                BackendConnectionState.Reconnecting, BackendConnectionState.Connecting, BackendConnectionState.Degraded,
                BackendConnectionState.Reconnecting, BackendConnectionState.Connecting, BackendConnectionState.Ready
            }, states);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Null(block.CurrentSession);
        Assert.False(block.IsReady);
    }

    [Fact]
    public async Task HealthLossPublishesNonreadyAndWaitsForConsumerRetirementBeforeSessionDisposal()
    {
        var processExit = new TaskCompletionSource<BackendServiceUnavailableEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new BackendServiceAvailabilityMonitor(_ => Task.FromResult(true),
            token => processExit.Task.WaitAsync(token),
            delayAsync: (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var session = new BackendServiceSession("http://127.0.0.1:1", availabilityMonitor: monitor);
        var ready = Signal();
        var lossPublished = Signal();
        var releaseConsumers = Signal();
        var delayEntered = Signal();
        BackendServiceUnavailableEventArgs? publishedLoss = null;
        var block = new NativeUiBackendConnectionBlock(() => session,
            async (state, value, generation, unavailable) =>
            {
                if (state == BackendConnectionState.Ready) ready.TrySetResult();
                if (state == BackendConnectionState.Degraded)
                {
                    Assert.Null(value);
                    publishedLoss = unavailable;
                    lossPublished.TrySetResult();
                    await releaseConsumers.Task;
                }
            }, connectAsync: (_, _) =>
            {
                monitor.Start();
                return Task.CompletedTask;
            }, delayAsync: (delay, token) =>
            {
                Assert.Equal(BackendReconnectPolicy.GetDelay(0), delay);
                delayEntered.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        using var stop = new CancellationTokenSource();
        var run = block.RunAsync(new(stop.Token, () => { }));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var loss = BackendServiceUnavailableEventArgs.OwnedProcessExited(42);
            processExit.TrySetResult(loss);
            await lossPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Same(loss, publishedLoss);
            Assert.False(block.IsReady);
            Assert.Same(session, block.CurrentSession);
            AssertSessionAlive(session);
            Assert.False(delayEntered.Task.IsCompleted);
            releaseConsumers.TrySetResult();
            await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(block.CurrentSession);
            AssertSessionDisposed(session);
            Assert.True(monitor.Completion.IsCompletedSuccessfully);
        }
        finally
        {
            releaseConsumers.TrySetResult();
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task StopWaitsForNonreadyPublicationAndTheNextRunUsesANewGeneration()
    {
        var readySessions = Channel.CreateUnbounded<BackendServiceSession>();
        var stopping = Signal();
        var releaseConsumers = Signal();
        var reports = 0;
        var block = new NativeUiBackendConnectionBlock(NewSession,
            async (state, session, generation, unavailable) =>
            {
                if (state == BackendConnectionState.Ready) readySessions.Writer.TryWrite(session!);
                if (state == BackendConnectionState.ShuttingDown)
                {
                    Assert.Null(session);
                    stopping.TrySetResult();
                    await releaseConsumers.Task;
                }
            }, connectAsync: (_, _) => Task.CompletedTask);
        using var firstStop = new CancellationTokenSource();
        var first = block.RunAsync(new(firstStop.Token, () => reports++));
        try
        {
            var session = await readySessions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, block.Generation);
            firstStop.Cancel();
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(first.IsCompleted);
            Assert.Same(session, block.CurrentSession);
            Assert.False(block.IsReady);
            AssertSessionAlive(session);
            releaseConsumers.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            AssertSessionDisposed(session);

            using var secondStop = new CancellationTokenSource();
            var second = block.RunAsync(new(secondStop.Token, () => reports++));
            try
            {
                var replacement = await readySessions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                Assert.NotSame(session, replacement);
                Assert.Equal(2, block.Generation);
                Assert.Equal(2, reports);
            }
            finally
            {
                secondStop.Cancel();
                await second.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            releaseConsumers.TrySetResult();
            firstStop.Cancel();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static BackendServiceSession NewSession() => new("http://127.0.0.1:1");

    private static void AssertSessionAlive(BackendServiceSession session) =>
        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/relative-request-is-rejected-before-network");
            _ = session.SendFrontendRequestAsync(request, CancellationToken.None);
        });

    private static void AssertSessionDisposed(BackendServiceSession session) =>
        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/relative-request-is-rejected-before-network");
            _ = session.SendFrontendRequestAsync(request, CancellationToken.None);
        });
}
