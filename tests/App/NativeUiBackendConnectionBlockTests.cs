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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerationStopsItsMonitorBeforePublishingUnavailable(bool stopping)
    {
        var processExit = new TaskCompletionSource<BackendServiceUnavailableEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var monitorEntered = Signal();
        var monitorCanceled = Signal();
        var releaseMonitor = Signal();
        var monitor = new BackendServiceAvailabilityMonitor(_ => Task.FromResult(true),
            token => processExit.Task.WaitAsync(token),
            delayAsync: async (_, token) =>
            {
                monitorEntered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally
                {
                    monitorCanceled.TrySetResult();
                    await releaseMonitor.Task;
                }
            });
        var session = new BackendServiceSession("http://127.0.0.1:1", availabilityMonitor: monitor);
        var ready = Signal();
        var unavailablePublished = Signal();
        var monitorEndedAtPublication = false;
        BackendServiceSession.Hold? acquiredAfterEnd = null;
        var block = new NativeUiBackendConnectionBlock(() => session,
            (state, value, generation, unavailable) =>
            {
                if (state == BackendConnectionState.Ready) ready.TrySetResult();
                if (state == (stopping ? BackendConnectionState.ShuttingDown : BackendConnectionState.Degraded))
                {
                    Assert.Null(value);
                    monitorEndedAtPublication = monitor.Completion.IsCompletedSuccessfully;
                    acquiredAfterEnd = session.TryAcquire();
                    unavailablePublished.TrySetResult();
                }
                return Task.CompletedTask;
            }, connectAsync: (_, _) =>
            {
                monitor.Start();
                return Task.CompletedTask;
            }, delayAsync: (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        using var stop = new CancellationTokenSource();
        var run = block.RunAsync(new(stop.Token, () => { }));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await monitorEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (stopping) stop.Cancel();
            else processExit.TrySetResult(BackendServiceUnavailableEventArgs.OwnedProcessExited(42));
            await monitorCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(unavailablePublished.Task.IsCompleted);
            releaseMonitor.TrySetResult();
            await unavailablePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(monitorEndedAtPublication);
            Assert.Null(acquiredAfterEnd);
        }
        finally
        {
            acquiredAfterEnd?.Dispose();
            releaseMonitor.TrySetResult();
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ReconnectAndFinalReturnDoNotWaitForConsumersHoldingEitherGeneration()
    {
        var processExit = new TaskCompletionSource<BackendServiceUnavailableEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new BackendServiceAvailabilityMonitor(_ => Task.FromResult(true),
            token => processExit.Task.WaitAsync(token),
            delayAsync: (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var readySessions = Channel.CreateUnbounded<BackendServiceSession>();
        var created = 0;
        var block = new NativeUiBackendConnectionBlock(() => ++created == 1
                ? new BackendServiceSession("http://127.0.0.1:1", availabilityMonitor: monitor) : NewSession(),
            (state, session, generation, unavailable) =>
            {
                if (state == BackendConnectionState.Ready) readySessions.Writer.TryWrite(session!);
                return Task.CompletedTask;
            }, connectAsync: (_, _) =>
            {
                if (created == 1) monitor.Start();
                return Task.CompletedTask;
            }, delayAsync: (_, _) => Task.CompletedTask);
        using var stop = new CancellationTokenSource();
        var run = block.RunAsync(new(stop.Token, () => { }));
        BackendServiceSession.Hold? firstHold = null;
        BackendServiceSession.Hold? nextHold = null;
        try
        {
            var session = await readySessions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            firstHold = Assert.IsType<BackendServiceSession.Hold>(block.TryAcquireCurrentSession());
            Assert.Same(session, firstHold.Session);
            Assert.Equal(1, block.Generation);
            processExit.TrySetResult(BackendServiceUnavailableEventArgs.OwnedProcessExited(42));
            var replacement = await readySessions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotSame(session, replacement);
            Assert.Equal(2, block.Generation);
            Assert.Same(replacement, block.CurrentSession);
            Assert.Null(session.TryAcquire());
            AssertSessionAlive(session);
            nextHold = Assert.IsType<BackendServiceSession.Hold>(block.TryAcquireCurrentSession());
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(block.CurrentSession);
            Assert.Null(block.TryAcquireCurrentSession());
            Assert.Null(replacement.TryAcquire());
            AssertSessionAlive(session);
            AssertSessionAlive(replacement);
            firstHold.Dispose();
            AssertSessionDisposed(session);
            AssertSessionAlive(replacement);
            nextHold.Dispose();
            AssertSessionDisposed(replacement);
        }
        finally
        {
            firstHold?.Dispose();
            nextHold?.Dispose();
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
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
