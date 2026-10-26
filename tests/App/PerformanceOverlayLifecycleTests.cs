using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using ResourceManager.NativeUi.Overlay;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlayLifecycleTests
{
    [Fact]
    public void StopJoinsSubscriptionCleanupAndRetiresQueuedCallbacksAcrossRestart() => RunSta(async () =>
    {
        var posted = new Queue<Action>();
        await using var overlay = new PerformanceOverlayCoordinator(posted.Enqueue);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var ownerThread = Environment.CurrentManagedThreadId;
        var cleanupThread = 0;
        var first = overlay.RunAsync(async (post, token) =>
        {
            post(() => calls++);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                canceled.TrySetResult();
                await release.Task;
                cleanupThread = Environment.CurrentManagedThreadId;
                post(() => calls++); // A finishing producer cannot post after Stop.
            }
        }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var stopping = overlay.StopAsync();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(stopping.IsCompleted);
            Assert.Same(first, overlay.RunAsync((_, _) => throw new InvalidOperationException("Overlapping run."),
                CancellationToken.None));
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ownerThread, cleanupThread);
            Assert.False(overlay.WindowRefreshTimerEnabled);

            using var cancellation = new CancellationTokenSource();
            var second = overlay.RunAsync((post, token) =>
            {
                post(() => calls += 10);
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, cancellation.Token);
            Assert.NotSame(first, second);
            while (posted.TryDequeue(out var callback)) callback();
            Assert.Equal(10, calls); // The queued first-run action was retired, not replayed.
            cancellation.Cancel();
            await second.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.TrySetResult();
            await overlay.StopAsync();
        }
    });

    [Fact]
    public void NaturalReturnRetiresItsCallbacksWithoutWaitingForUiDispatch() => RunSta(async () =>
    {
        var posted = new Queue<Action>();
        await using var overlay = new PerformanceOverlayCoordinator(posted.Enqueue);
        var calls = 0;
        await overlay.RunAsync((post, _) =>
        {
            post(() => calls++);
            return Task.CompletedTask;
        }, CancellationToken.None);
        while (posted.TryDequeue(out var callback)) callback();
        Assert.Equal(0, calls);
        Assert.False(overlay.WindowRefreshTimerEnabled);
    });

    // An STA and a managed queue suffice: no Form, HWND, service or real desktop input.
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Nonvisual overlay lifecycle test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class QueuedContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> queue = new();
        public override void Post(SendOrPostCallback callback, object? state)
            => queue.Add(() => callback(state));
        internal void Complete() => queue.CompleteAdding();
        internal void Pump()
        {
            foreach (var action in queue.GetConsumingEnumerable()) action();
        }
        public void Dispose() => queue.Dispose();
    }
}
