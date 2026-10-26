using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.Blocks;
using ResourceManager.Shared.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class NativeUiSessionRetirementIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string[] ConsumerIds =
        [NativeUiBlockIds.DesktopObservation, NativeUiBlockIds.Overlay, NativeUiBlockIds.TrayNotifications];

    [Fact]
    public void ConnectionRetiresBothTreeConsumersBeforeDisposalAndReconnectKeepsWindowBranch()
        => RunSta(async () =>
        {
            var ui = SynchronizationContext.Current!;
            var sessions = Channel.CreateUnbounded<SessionRun>();
            var executions = new ConcurrentDictionary<string, Channel<Execution>>();
            var counts = new ConcurrentDictionary<string, int>();
            var monitors = new Dictionary<BackendServiceSession, BackendServiceAvailabilityMonitor>();
            var cleanup = ConsumerIds.ToDictionary(id => id, _ => Signal());
            var reconnectDelayEntered = Signal();
            NativeUiBlockTrees? trees = null;
            NativeUiBackendConnectionBlock? connection = null;

            connection = new NativeUiBackendConnectionBlock(() =>
            {
                var exited = new TaskCompletionSource<BackendServiceUnavailableEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var monitor = new BackendServiceAvailabilityMonitor(_ => Task.FromResult(true),
                    token => exited.Task.WaitAsync(token),
                    delayAsync: (_, token) => Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token));
                var session = new BackendServiceSession("http://127.0.0.1:1", availabilityMonitor: monitor);
                monitors.Add(session, monitor);
                sessions.Writer.TryWrite(new(session, exited));
                return session;
            }, async (state, session, generation, unavailable) =>
            {
                if (state == BackendConnectionState.Ready)
                {
                    await trees!.SetBackendSessionReadyAsync(true, generation);
                }
                else if (state is BackendConnectionState.Degraded or BackendConnectionState.ShuttingDown)
                {
                    // Match the actual composition boundary, including the cross-tree tray consumer.
                    await trees!.SetBackendSessionReadyAsync(false, generation);
                    await Task.WhenAll(ConsumerIds.Select(id =>
                        trees!.WaitForStateAsync(id, BusinessBlockState.Stopped)));
                }
            }, connectAsync: (session, _) =>
            {
                monitors[session].Start();
                return Task.CompletedTask;
            }, delayAsync: (_, token) =>
            {
                reconnectDelayEntered.TrySetResult();
                return Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            });

            Channel<Execution> Runs(string id) => executions.GetOrAdd(id, _ => Channel.CreateUnbounded<Execution>());
            async Task<Execution> Next(string id) => await Runs(id).Reader.ReadAsync().AsTask().WaitAsync(Timeout);

            async Task RunProbe(string id, BusinessBlockRunContext context)
            {
                var execution = new Execution(ConsumerIds.Contains(id) ? connection!.CurrentSession : null);
                counts.AddOrUpdate(id, 1, (_, previous) => previous + 1);
                context.ReportRunning();
                Runs(id).Writer.TryWrite(execution);
                try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, context.StopNotification); }
                catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
                execution.StopObserved.TrySetResult();
                if (cleanup.TryGetValue(id, out var release)) await release.Task;
            }

            trees = new NativeUiBlockTrees(id => new OnUi(ui, context => id == NativeUiBlockIds.BackendConnection
                    ? connection!.RunAsync(context) : RunProbe(id, context)),
                NullLogger.Instance, NullLogger<BusinessBlockController>.Instance);
            try
            {
                await trees.StartAsync(true);
                var window = await Next(NativeUiBlockIds.MainWindow);
                var webView = await Next(NativeUiBlockIds.WebViewHost);
                var firstSession = await sessions.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
                var firstConsumers = new Dictionary<string, Execution>();
                foreach (var id in ConsumerIds)
                {
                    var execution = await Next(id);
                    firstConsumers.Add(id, execution);
                    Assert.Same(firstSession.Session, execution.Session);
                }
                Assert.Equal(1, connection.Generation);
                firstSession.Exited.TrySetResult(BackendServiceUnavailableEventArgs.OwnedProcessExited(12));
                await Task.WhenAll(firstConsumers.Values.Select(value => value.StopObserved.Task)).WaitAsync(Timeout);

                Assert.False(connection.IsReady);
                Assert.Same(firstSession.Session, connection.CurrentSession);
                AssertSessionAlive(firstSession.Session);
                Assert.False(reconnectDelayEntered.Task.IsCompleted);
                Assert.False(window.StopObserved.Task.IsCompleted);
                Assert.False(webView.StopObserved.Task.IsCompleted);

                // Releasing the backend subtree alone must not dispose the shared session:
                // the independent tray tree is still finishing its own consumer.
                foreach (var id in ConsumerIds.Take(2))
                {
                    cleanup[id].TrySetResult();
                    await trees.WaitForStateAsync(id, BusinessBlockState.Stopped).WaitAsync(Timeout);
                    AssertSessionAlive(firstSession.Session);
                    Assert.False(reconnectDelayEntered.Task.IsCompleted);
                }
                Assert.Equal(BusinessBlockState.Stopping, trees.ReadState(NativeUiBlockIds.TrayNotifications));
                cleanup[NativeUiBlockIds.TrayNotifications].TrySetResult();
                await reconnectDelayEntered.Task.WaitAsync(Timeout);
                AssertSessionDisposed(firstSession.Session);
                Assert.Null(connection.CurrentSession);

                connection.RequestRetry();
                var secondSession = await sessions.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
                foreach (var id in ConsumerIds)
                {
                    var replacement = await Next(id);
                    Assert.NotSame(firstConsumers[id], replacement);
                    Assert.Same(secondSession.Session, replacement.Session);
                }
                Assert.NotSame(firstSession.Session, secondSession.Session);
                Assert.Equal(2, connection.Generation);
                Assert.True(connection.IsReady);
                Assert.Equal(1, counts[NativeUiBlockIds.MainWindow]);
                Assert.Equal(1, counts[NativeUiBlockIds.WebViewHost]);
                Assert.Equal(BusinessBlockState.Running, trees.ReadState(NativeUiBlockIds.MainWindow));
                Assert.False(window.StopObserved.Task.IsCompleted);
                Assert.False(webView.StopObserved.Task.IsCompleted);
            }
            finally
            {
                foreach (var release in cleanup.Values) release.TrySetResult();
                await trees.DisposeAsync().AsTask().WaitAsync(Timeout);
            }
            Assert.All(trees.Read().Blocks, value => Assert.Equal(BusinessBlockState.Stopped, value.State));
            Assert.Null(connection.CurrentSession);
            Assert.False(connection.IsReady);
        });

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertSessionAlive(BackendServiceSession session) =>
        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/rejected-before-network");
            _ = session.SendFrontendRequestAsync(request, CancellationToken.None);
        });

    private static void AssertSessionDisposed(BackendServiceSession session) =>
        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/rejected-before-network");
            _ = session.SendFrontendRequestAsync(request, CancellationToken.None);
        });

    private sealed record SessionRun(BackendServiceSession Session,
        TaskCompletionSource<BackendServiceUnavailableEventArgs> Exited);
    private sealed class Execution(BackendServiceSession? session)
    {
        internal BackendServiceSession? Session { get; } = session;
        internal TaskCompletionSource StopObserved { get; } = Signal();
    }

    private sealed class OnUi(SynchronizationContext ui, Func<BusinessBlockRunContext, Task> run) : IBusinessBlock
    {
        public Task RunAsync(BusinessBlockRunContext context)
        {
            var finished = Signal();
            ui.Post(async _ =>
            {
                try { await run(context); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
            }, null);
            return finished.Task;
        }
    }

    // This queue models the single UI context without creating a Form, HWND or service.
    private static void RunSta(Func<Task> scenario)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var context = new QueuedContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var run = scenario();
                _ = run.ContinueWith(_ => context.Complete(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                context.Pump();
                run.GetAwaiter().GetResult();
            }
            catch (Exception error) { failure = error; }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "Session retirement integration timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class QueuedContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => queue.Add(() => callback(state));
        internal void Complete() => queue.CompleteAdding();
        internal void Pump()
        {
            foreach (var action in queue.GetConsumingEnumerable()) action();
        }
        public void Dispose() => queue.Dispose();
    }
}
