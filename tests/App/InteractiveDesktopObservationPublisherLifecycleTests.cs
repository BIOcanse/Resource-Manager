using System.Collections.Concurrent;
using ResourceManager.NativeUi.SystemIntegration;
using ResourceManager.Shared.Runtime;

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

    [Fact]
    public async Task StreamWritesOnlyChangedContentAndAReopenedStreamStartsWithTheCurrentSample()
    {
        await using var publisher = new InteractiveDesktopObservationPublisher();
        var captures = 0;
        var foreground = 100;
        var open = 0;
        var overlapped = false;
        var streams = new ConcurrentQueue<RecordedStream>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = publisher.RunAsync(
            // Every capture carries a new ObservedAt; only the content decides whether a line is written.
            () => Sample(Volatile.Read(ref foreground), Interlocked.Increment(ref captures)),
            async (writeLines, token) =>
            {
                if (Interlocked.Increment(ref open) != 1) overlapped = true;
                var stream = new RecordedStream();
                streams.Enqueue(stream);
                using var body = CancellationTokenSource.CreateLinkedTokenSource(token, stream.End.Token);
                try
                {
                    await writeLines((sample, _) =>
                    {
                        if (stream.FailWrites) throw new IOException("The stream was reset.");
                        stream.Lines.Enqueue(sample);
                        return Task.CompletedTask;
                    }, body.Token);
                }
                // The request ended under the body.
                catch (OperationCanceledException) when (stream.End.IsCancellationRequested) { }
                finally { Interlocked.Decrement(ref open); }
            }, stop.Token);
        try
        {
            await Wait(() => streams.Count == 1 && streams.First().Lines.Count == 1, stop.Token);
            var first = streams.First();
            Assert.Equal(100, first.Lines.Single().Foreground!.ProcessId);
            await WaitForCaptures(3);
            Assert.Single(first.Lines);

            Volatile.Write(ref foreground, 200);
            await Wait(() => first.Lines.Count == 2, stop.Token);
            Assert.Equal(200, first.Lines.Last().Foreground!.ProcessId);
            await WaitForCaptures(3);
            Assert.Equal(2, first.Lines.Count);

            // The request ends: a new stream opens and starts with the current sample, although its
            // content equals the last line of the previous stream.
            first.End.Cancel();
            await Wait(() => streams.Count == 2 && streams.ElementAt(1).Lines.Count == 1, stop.Token);
            var second = streams.ElementAt(1);
            Assert.Equal(200, second.Lines.Single().Foreground!.ProcessId);
            await WaitForCaptures(3);
            Assert.Single(second.Lines);
            Assert.Equal(2, first.Lines.Count);

            // A failed write ends the stream too; the next stream starts with the current sample.
            second.FailWrites = true;
            Volatile.Write(ref foreground, 100);
            await Wait(() => streams.Count == 3 && streams.ElementAt(2).Lines.Count == 1, stop.Token);
            Assert.Equal(100, streams.ElementAt(2).Lines.Single().Foreground!.ProcessId);
            Assert.Single(second.Lines);
            Assert.False(overlapped);
        }
        finally
        {
            stop.Cancel();
            await run;
        }

        async Task WaitForCaptures(int count)
        {
            var target = Volatile.Read(ref captures) + count;
            await Wait(() => Volatile.Read(ref captures) >= target, stop.Token);
        }
    }

    private static InteractiveDesktopObservation Sample(int foreground, long observedAt)
        => new(999, 1, 1, observedAt, true, true, new DesktopProcessWindowObservation(foreground, 1, true, false),
            [new DesktopProcessWindowObservation(100, 1, true, false), new DesktopProcessWindowObservation(200, 1, true, false)]);

    private static async Task Wait(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }

    private sealed class RecordedStream
    {
        private volatile bool failWrites;
        internal ConcurrentQueue<InteractiveDesktopObservation> Lines { get; } = new();
        internal CancellationTokenSource End { get; } = new();
        internal bool FailWrites { get => failWrites; set => failWrites = value; }
    }
}
