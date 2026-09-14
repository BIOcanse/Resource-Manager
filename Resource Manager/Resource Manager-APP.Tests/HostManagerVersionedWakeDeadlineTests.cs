using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerVersionedWakeDeadlineTests
{
    [Fact]
    public async Task EarlierForegroundDeadlineRebasesScheduledWait()
    {
        var time = new ManualTimeProvider();
        var delays = new ControlledDelayQueue();
        var deadline = new HostManagerVersionedWakeDeadline(
            time,
            delays.DelayAsync);
        deadline.PublishScheduled(TimeSpan.FromSeconds(60));

        var wait = deadline.WaitAsync(CancellationToken.None);
        var original = await delays.WaitForRequestAsync(0);
        Assert.Equal(TimeSpan.FromSeconds(60), original.Delay);

        var publication = deadline.PublishForeground(TimeSpan.FromSeconds(1));

        var rebased = await delays.WaitForRequestAsync(1);
        Assert.True(original.Completion.IsCanceled);
        Assert.Equal(2UL, publication.Version);
        Assert.Equal(TimeSpan.FromSeconds(1), rebased.Delay);
        time.Advance(TimeSpan.FromSeconds(1));
        rebased.Complete();

        var consumed = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(publication, consumed);
        Assert.Equal(TimeSpan.TicksPerSecond, deadline.TimestampFrequency);
        Assert.Equal(time.GetTimestamp(), deadline.GetTimestamp());
        Assert.False(deadline.Snapshot.HasDeadline);
    }

    [Fact]
    public async Task LaterForegroundDeadlineCannotPostponeScheduledWait()
    {
        var time = new ManualTimeProvider();
        var delays = new ControlledDelayQueue();
        var deadline = new HostManagerVersionedWakeDeadline(
            time,
            delays.DelayAsync);
        deadline.PublishScheduled(TimeSpan.FromSeconds(1));

        var wait = deadline.WaitAsync(CancellationToken.None);
        var original = await delays.WaitForRequestAsync(0);

        var publication = deadline.PublishForeground(TimeSpan.FromSeconds(60));

        Assert.Equal(2UL, publication.Version);
        Assert.Equal(TimeSpan.FromSeconds(1).Ticks, publication.DeadlineTimestamp);
        Assert.Single(delays.Requests);
        time.Advance(TimeSpan.FromSeconds(1));
        original.Complete();

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(delays.Requests);
        Assert.False(deadline.Snapshot.HasDeadline);
    }

    [Fact]
    public async Task CoalescedForegroundPublicationsRetainEarliestDeadline()
    {
        var time = new ManualTimeProvider();
        var delays = new ControlledDelayQueue();
        var deadline = new HostManagerVersionedWakeDeadline(
            time,
            delays.DelayAsync);
        deadline.PublishScheduled(TimeSpan.FromSeconds(60));

        var wait = deadline.WaitAsync(CancellationToken.None);
        _ = await delays.WaitForRequestAsync(0);
        deadline.PublishForeground(TimeSpan.FromSeconds(10));
        deadline.PublishForeground(TimeSpan.FromSeconds(5));
        var publication = deadline.PublishForeground(TimeSpan.FromSeconds(8));

        var rebased = await delays.WaitForRequestAsync(1);
        Assert.Equal(4UL, publication.Version);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, publication.DeadlineTimestamp);
        Assert.Equal(TimeSpan.FromSeconds(5), rebased.Delay);
        time.Advance(TimeSpan.FromSeconds(5));
        rebased.Complete();

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(deadline.Snapshot.HasDeadline);
    }

    [Fact]
    public async Task StaleSignalCannotSkipReplacementScheduledDeadline()
    {
        var time = new ManualTimeProvider();
        var delays = new ControlledDelayQueue();
        var deadline = new HostManagerVersionedWakeDeadline(
            time,
            delays.DelayAsync);
        deadline.PublishForeground(TimeSpan.FromSeconds(1));
        var publication = deadline.PublishScheduled(TimeSpan.FromSeconds(30));

        var wait = deadline.WaitAsync(CancellationToken.None);
        var stale = await delays.WaitForRequestAsync(0);
        var scheduled = await delays.WaitForRequestAsync(1);

        Assert.Equal(2UL, publication.Version);
        Assert.True(stale.Completion.IsCanceled);
        Assert.Equal(TimeSpan.FromSeconds(30), scheduled.Delay);
        time.Advance(TimeSpan.FromSeconds(30));
        scheduled.Complete();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(deadline.Snapshot.HasDeadline);
    }

    [Fact]
    public async Task PublicationAtTimeoutBoundaryIsNeverLost()
    {
        var time = new ManualTimeProvider();
        var delays = new ControlledDelayQueue();
        var deadline = new HostManagerVersionedWakeDeadline(
            time,
            delays.DelayAsync);
        deadline.PublishScheduled(TimeSpan.FromSeconds(1));

        var firstWait = deadline.WaitAsync(CancellationToken.None);
        var original = await delays.WaitForRequestAsync(0);
        time.Advance(TimeSpan.FromSeconds(1));
        original.Complete();
        await firstWait.WaitAsync(TimeSpan.FromSeconds(5));

        var publication = deadline.PublishForeground(TimeSpan.Zero);
        Assert.True(publication.HasDeadline);
        var boundaryWait = deadline.WaitAsync(CancellationToken.None);
        var consumed = await boundaryWait.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2UL, publication.Version);
        Assert.Equal(publication, consumed);
        Assert.False(deadline.Snapshot.HasDeadline);
    }

    [Fact]
    public async Task EarlyDelayCompletionCannotConsumeUnexpiredDeadline()
    {
        var time = new ManualTimeProvider();
        var delays = new ControlledDelayQueue();
        var deadline = new HostManagerVersionedWakeDeadline(
            time,
            delays.DelayAsync);
        var publication = deadline.PublishScheduled(TimeSpan.FromSeconds(1));

        var wait = deadline.WaitAsync(CancellationToken.None);
        var first = await delays.WaitForRequestAsync(0);
        time.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        first.Complete();

        var remainder = await delays.WaitForRequestAsync(1);
        Assert.Equal(TimeSpan.FromTicks(1), remainder.Delay);
        Assert.Equal(publication, deadline.Snapshot);
        time.Advance(TimeSpan.FromTicks(1));
        remainder.Complete();

        var consumed = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(publication, consumed);
        Assert.False(deadline.Snapshot.HasDeadline);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            timestamp = checked(timestamp + value.Ticks);
        }
    }

    private sealed class ControlledDelayQueue
    {
        private readonly object sync = new();
        private readonly List<ControlledDelayRequest> requests = [];
        private TaskCompletionSource requestAdded = CreateSignal();

        internal IReadOnlyList<ControlledDelayRequest> Requests
        {
            get
            {
                lock (sync)
                {
                    return requests.ToArray();
                }
            }
        }

        internal Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var request = new ControlledDelayRequest(delay, cancellationToken);
            TaskCompletionSource added;
            lock (sync)
            {
                requests.Add(request);
                added = requestAdded;
                requestAdded = CreateSignal();
            }
            added.TrySetResult();
            return request.Completion;
        }

        internal async Task<ControlledDelayRequest> WaitForRequestAsync(int index)
        {
            while (true)
            {
                Task wait;
                lock (sync)
                {
                    if (requests.Count > index)
                    {
                        return requests[index];
                    }
                    wait = requestAdded.Task;
                }
                await wait.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static TaskCompletionSource CreateSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledDelayRequest
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ControlledDelayRequest(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            Delay = delay;
            _ = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
        }

        internal TimeSpan Delay { get; }

        internal Task Completion => completion.Task;

        internal void Complete() => completion.TrySetResult();
    }
}
