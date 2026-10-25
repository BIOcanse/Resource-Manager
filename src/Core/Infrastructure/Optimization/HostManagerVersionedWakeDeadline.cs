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
    private ulong version;
    private bool hasDeadline;
    private long deadlineTimestamp;
    private int wakePosted;

    internal HostManagerVersionedWakeDeadline(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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
        => PublishScheduled((TimeSpan?)delay);

    internal HostManagerWakeDeadlineSnapshot PublishScheduled(TimeSpan? delay)
    {
        var candidate = delay is { } value ? CreateDeadline(value) : 0;
        lock (sync)
        {
            version = NextVersion(version);
            hasDeadline = delay is not null;
            deadlineTimestamp = candidate;
            return CaptureSnapshot();
        }
    }

    internal bool ClearScheduled(ulong expectedVersion)
    {
        lock (sync)
        {
            if (version != expectedVersion || !hasDeadline)
            {
                return false;
            }

            version = NextVersion(version);
            hasDeadline = false;
            deadlineTimestamp = 0;
            return true;
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
            await WaitForSignalOrDelayAsync(remaining, cancellationToken).ConfigureAwait(false);
            // Re-read the clock/version after either wake. A timer can complete
            // one provider timestamp quantum early; it never consumes a deadline.
        }
    }

    private async Task WaitForSignalOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await using var timer = timeProvider.CreateTimer(static owner =>
            ((HostManagerVersionedWakeDeadline)owner!).Signal(), this, delay, Timeout.InfiniteTimeSpan);
        await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref wakePosted, 0);
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

}
