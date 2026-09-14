namespace ResourceManager.App.Infrastructure.Optimization;

internal readonly record struct HostManagerWakeDeadlineSnapshot(
    ulong Version,
    bool HasDeadline,
    long DeadlineTimestamp);

internal sealed class HostManagerVersionedWakeDeadline
{
    private readonly object sync = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly TimeProvider timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private ulong version;
    private bool hasDeadline;
    private long deadlineTimestamp;
    private int wakePosted;

    internal HostManagerVersionedWakeDeadline(TimeProvider timeProvider)
        : this(
            timeProvider,
            (delay, cancellationToken) => Task.Delay(
                delay,
                timeProvider,
                cancellationToken))
    {
    }

    internal HostManagerVersionedWakeDeadline(
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        this.timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        this.delayAsync = delayAsync
            ?? throw new ArgumentNullException(nameof(delayAsync));
        if (timeProvider.TimestampFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeProvider),
                "The wake deadline requires a positive timestamp frequency.");
        }
    }

    internal HostManagerWakeDeadlineSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                return CaptureSnapshot();
            }
        }
    }

    internal long TimestampFrequency => timeProvider.TimestampFrequency;

    internal long GetTimestamp() => timeProvider.GetTimestamp();

    internal HostManagerWakeDeadlineSnapshot PublishScheduled(TimeSpan delay)
    {
        var candidate = CreateDeadline(delay);
        lock (sync)
        {
            version = NextVersion(version);
            hasDeadline = true;
            deadlineTimestamp = candidate;
            return CaptureSnapshot();
        }
    }

    internal HostManagerWakeDeadlineSnapshot PublishForeground(TimeSpan delay)
    {
        var candidate = CreateDeadline(delay);
        bool wake;
        HostManagerWakeDeadlineSnapshot snapshot;
        lock (sync)
        {
            version = NextVersion(version);
            wake = !hasDeadline || candidate < deadlineTimestamp;
            if (wake)
            {
                hasDeadline = true;
                deadlineTimestamp = candidate;
            }
            snapshot = CaptureSnapshot();
        }

        if (wake)
        {
            Signal();
        }
        return snapshot;
    }

    internal async Task<HostManagerWakeDeadlineSnapshot> WaitAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observed = Snapshot;
            if (!observed.HasDeadline)
            {
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref wakePosted, 0);
                continue;
            }

            var now = timeProvider.GetTimestamp();
            if (now >= observed.DeadlineTimestamp)
            {
                if (TryConsume(observed))
                {
                    return observed;
                }
                continue;
            }

            var remaining = timeProvider.GetElapsedTime(
                now,
                observed.DeadlineTimestamp);
            var outcome = await WaitForSignalOrDelayAsync(
                remaining,
                cancellationToken).ConfigureAwait(false);
            if (outcome == HostManagerWakeWaitOutcome.Delay)
            {
                // Task.Delay can complete up to one provider timestamp quantum before
                // the requested deadline because the provider interval is converted to
                // TimeSpan ticks. Re-sample the provider clock before consuming so the
                // scheduler never returns an unexpired deadline.
                continue;
            }
        }
    }

    private async Task<HostManagerWakeWaitOutcome> WaitForSignalOrDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        using var delayCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var signalCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = delayAsync(delay, delayCancellation.Token);
        var signalTask = signal.WaitAsync(signalCancellation.Token);
        var completed = await Task.WhenAny(delayTask, signalTask).ConfigureAwait(false);
        if (completed == delayTask)
        {
            signalCancellation.Cancel();
            try
            {
                await delayTask.ConfigureAwait(false);
            }
            finally
            {
                await ObserveCancelledLoserAsync(signalTask).ConfigureAwait(false);
            }
            return HostManagerWakeWaitOutcome.Delay;
        }

        delayCancellation.Cancel();
        try
        {
            await signalTask.ConfigureAwait(false);
            Interlocked.Exchange(ref wakePosted, 0);
        }
        finally
        {
            await ObserveCancelledLoserAsync(delayTask).ConfigureAwait(false);
        }
        return HostManagerWakeWaitOutcome.Signal;
    }

    private bool TryConsume(HostManagerWakeDeadlineSnapshot observed)
    {
        lock (sync)
        {
            if (!hasDeadline
                || version != observed.Version
                || deadlineTimestamp != observed.DeadlineTimestamp)
            {
                return false;
            }

            hasDeadline = false;
            deadlineTimestamp = 0;
            return true;
        }
    }

    private long CreateDeadline(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delay),
                "The wake delay cannot be negative.");
        }

        var timestamp = timeProvider.GetTimestamp();
        if (delay == TimeSpan.Zero)
        {
            return timestamp;
        }

        var numerator = (Int128)delay.Ticks * timeProvider.TimestampFrequency;
        var delta = (numerator + TimeSpan.TicksPerSecond - 1)
            / TimeSpan.TicksPerSecond;
        if (delta > long.MaxValue)
        {
            return long.MaxValue;
        }

        var boundedDelta = (long)delta;
        return timestamp > long.MaxValue - boundedDelta
            ? long.MaxValue
            : timestamp + boundedDelta;
    }

    private HostManagerWakeDeadlineSnapshot CaptureSnapshot()
        => new(version, hasDeadline, deadlineTimestamp);

    private void Signal()
    {
        if (Interlocked.Exchange(ref wakePosted, 1) != 0)
        {
            return;
        }
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A full semaphore already represents the coalesced wake request.
        }
    }

    private static ulong NextVersion(ulong current)
    {
        if (current == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "The Host Manager wake deadline version wrapped to zero.");
        }
        return current + 1;
    }

    private static async Task ObserveCancelledLoserAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private enum HostManagerWakeWaitOutcome
    {
        Signal = 0,
        Delay = 1
    }
}
