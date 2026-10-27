using System.Threading.Channels;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class ControlActualStateReaderTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SingleFetchWaitsForNewWriterResultAndWithdrawsDemandAfterDeliveryOrCancellation()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        await reader.StartAsync(default);
        try
        {
            var first = reader.FetchOnceAsync("control-test:first", default);
            var startup = await clock.NextTimerAsync();
            Assert.False(first.IsCompleted);
            startup.Fire();
            var periodic = await clock.NextTimerAsync();
            Assert.NotNull(await first.WaitAsync(Deadline));
            await WaitUntilAsync(() => periodic.Disposed);
            Assert.Equal(1, writer.Reads);

            writer.Read = _ => Task.FromResult<ControlActualValue?>(new("device", "power", 77));
            var second = reader.FetchOnceAsync("control-test:second", default);
            var resumed = await clock.NextTimerAsync();
            Assert.False(second.IsCompleted); // The retained result must not satisfy a new demand.
            resumed.Fire();
            var resumedPeriodic = await clock.NextTimerAsync();
            Assert.Equal(77, Assert.Single((await second.WaitAsync(Deadline))!.Values).Number);
            await WaitUntilAsync(() => resumedPeriodic.Disposed);

            using var cancellation = new CancellationTokenSource();
            var cancelled = reader.FetchOnceAsync("control-test:cancel", cancellation.Token);
            var cancelledStartup = await clock.NextTimerAsync();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            await WaitUntilAsync(() => cancelledStartup.Disposed);
            Assert.Equal(2, writer.Reads);
        }
        finally { await reader.StopAsync(default); }
    }

    [Fact]
    public async Task PreviousRunLeaseCannotCancelFreshDemandAfterRestart()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        await reader.StartAsync(default);
        using var previous = reader.AcquireSubscription();
        _ = await clock.NextTimerAsync();
        await reader.StopAsync(default);

        await reader.StartAsync(default);
        try
        {
            using var current = reader.AcquireSubscription();
            var startup = await clock.NextTimerAsync();
            previous.Dispose();
            startup.Fire();
            _ = await clock.NextTimerAsync();
            await WaitUntilAsync(() => reader.Current.Values.Count == 1);
            Assert.Equal(1, writer.Reads);
            Assert.Single(reader.Current.Values);
        }
        finally { await reader.StopAsync(default); }
    }

    [Fact]
    public async Task SameReaderRestartsAndPublishesNewHardwareReadAfterStop()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        await reader.StartAsync(default);
        using (reader.AcquireSubscription())
        {
            (await clock.NextTimerAsync()).Fire();
            _ = await clock.NextTimerAsync();
            await WaitUntilAsync(() => reader.Current.Values.Count == 1);
            await reader.StopAsync(default);
        }
        var first = reader.Current;
        writer.Read = _ => Task.FromResult<ControlActualValue?>(new("device", "power", 77));
        await reader.StartAsync(default);
        try
        {
            using var demand = reader.AcquireSubscription();
            (await clock.NextTimerAsync()).Fire();
            _ = await clock.NextTimerAsync();
            await WaitUntilAsync(() => !ReferenceEquals(first, reader.Current));
            Assert.Equal(2, writer.Reads);
            Assert.Equal(77, Assert.Single(reader.Current.Values).Number);
        }
        finally { await reader.StopAsync(default); }
    }

    [Fact]
    public async Task NoSubscriptionDoesNotProbeOrReadWriters()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        var catalog = new Catalog();
        using var reader = new ControlActualStateReader(catalog, [writer], clock);
        await reader.StartAsync(default);
        clock.Advance(TimeSpan.FromHours(1));
        _ = reader.Current;
        Assert.Equal(0, catalog.Reads);
        Assert.Equal(0, writer.Probes);
        Assert.Equal(0, writer.Reads);
        Assert.Equal(0, clock.CreatedTimers);
        Assert.Same(ControlActualState.Empty, reader.Current);
        await reader.StopAsync(default);
    }

    [Fact]
    public async Task LeasesShareStartupAndIntervalAndLastUnsubscribeRetainsCurrent()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        await reader.StartAsync(default);
        using var first = reader.AcquireSubscription();
        var startup = await clock.NextTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(12), startup.DueTime);
        Assert.Equal(0, writer.Reads);
        using var second = reader.AcquireSubscription();
        startup.Fire();
        var periodic = await clock.NextTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(10), periodic.Period);
        await WaitUntilAsync(() => writer.Reads == 1 && reader.Current.Values.Count == 1);
        var initial = reader.Current;

        first.Dispose();
        first.Dispose(); // Lease disposal is idempotent; the second subscriber stays active.
        periodic.Fire();
        await WaitUntilAsync(() => writer.Reads == 2 && !ReferenceEquals(initial, reader.Current));
        var latest = reader.Current;
        second.Dispose();
        await WaitUntilAsync(() => periodic.Disposed);
        periodic.Fire();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, writer.Reads);
        Assert.Same(latest, reader.Current);

        using var resumed = reader.AcquireSubscription();
        var resumedStartup = await clock.NextTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(12), resumedStartup.DueTime);
        Assert.Same(latest, reader.Current);
        resumedStartup.Fire();
        _ = await clock.NextTimerAsync();
        await WaitUntilAsync(() => writer.Reads == 3 && !ReferenceEquals(latest, reader.Current));
        await reader.StopAsync(default);
    }

    [Fact]
    public async Task DemandEndingDuringStartupDoesNotReadAndResubscriptionGetsItsOwnDelay()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        await reader.StartAsync(default);
        var lease = reader.AcquireSubscription();
        var startup = await clock.NextTimerAsync();
        lease.Dispose();
        using var resumed = reader.AcquireSubscription();
        var nextStartup = await clock.NextTimerAsync();
        startup.Fire();
        Assert.Equal(0, writer.Reads);
        Assert.Same(ControlActualState.Empty, reader.Current);
        nextStartup.Fire();
        _ = await clock.NextTimerAsync();
        await WaitUntilAsync(() => writer.Reads == 1);
        await reader.StopAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedEmptyOrFailedReadReplacesThePreviousValue(bool fail)
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        await reader.StartAsync(default);
        using var lease = reader.AcquireSubscription();
        (await clock.NextTimerAsync()).Fire();
        var periodic = await clock.NextTimerAsync();
        await WaitUntilAsync(() => reader.Current.Values.Count == 1);
        var previous = reader.Current;
        writer.Read = _ => fail
            ? Task.FromException<ControlActualValue?>(new IOException("Hardware read failed."))
            : Task.FromResult<ControlActualValue?>(null);
        periodic.Fire();
        await WaitUntilAsync(() => !ReferenceEquals(previous, reader.Current));
        Assert.Empty(reader.Current.Values);
        Assert.True(reader.Current.ReadAt > previous.ReadAt);
        await reader.StopAsync(default);
    }

    [Fact]
    public async Task UnsubscribeCancelsInFlightReadAndRejectsItsLateResult()
    {
        var clock = new ControlledClock();
        var pending = new TaskCompletionSource<ControlActualValue?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new CountingWriter { Read = _ => pending.Task };
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        await reader.StartAsync(default);
        var lease = reader.AcquireSubscription();
        (await clock.NextTimerAsync()).Fire();
        var periodic = await clock.NextTimerAsync();
        await WaitUntilAsync(() => writer.Reads == 1);
        lease.Dispose();
        Assert.True(writer.LastToken.IsCancellationRequested);
        pending.SetResult(new("device", "power", 90)); // A native operation may finish after cancellation.
        await WaitUntilAsync(() => periodic.Disposed);
        Assert.Same(ControlActualState.Empty, reader.Current);
        Assert.Equal(1, writer.Reads);
        await reader.StopAsync(default);
        Assert.Throws<ObjectDisposedException>(() => reader.AcquireSubscription());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class Catalog : IControlObjectCatalog
    {
        public int Reads;
        public ControlObjectCatalog ReadObjects()
        {
            Interlocked.Increment(ref Reads);
            return new([new ControlObject("device", ControlObjectKinds.Gpu, "GPU",
                new ControlObjectPlatform(ControlOperatingSystems.Windows, ControlVendors.Nvidia),
                [new ControlCapability("power", ControlValueKinds.Number, Supported: true)])],
                DateTimeOffset.UnixEpoch);
        }
    }

    private sealed class CountingWriter : IControlWriter
    {
        public int Probes;
        public int Reads;
        public CancellationToken LastToken;
        public Func<CancellationToken, Task<ControlActualValue?>> Read =
            _ => Task.FromResult<ControlActualValue?>(new("device", "power", 42));
        public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
        {
            Interlocked.Increment(ref Probes);
            return ControlWriteAvailability.Yes();
        }
        public Task<ControlActualValue?> ReadAsync(ControlObject target, ControlCapability capability, CancellationToken token)
        {
            LastToken = token;
            Interlocked.Increment(ref Reads);
            return Read(token);
        }
        public Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability,
            ControlSetting setting, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class ControlledClock : TimeProvider
    {
        private readonly Channel<ControlledTimer> timers = Channel.CreateUnbounded<ControlledTimer>();
        private long ticks = DateTimeOffset.UnixEpoch.UtcTicks;
        public int CreatedTimers;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan time) => Interlocked.Add(ref ticks, time.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(this, callback, state, dueTime, period);
            Interlocked.Increment(ref CreatedTimers);
            timers.Writer.TryWrite(timer);
            return timer;
        }
        public async Task<ControlledTimer> NextTimerAsync()
            => await timers.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
    }

    private sealed class ControlledTimer(ControlledClock clock, TimerCallback callback, object? state,
        TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private int disposed;
        public TimeSpan DueTime { get; private set; } = dueTime;
        public TimeSpan Period { get; private set; } = period;
        public bool Disposed => Volatile.Read(ref disposed) != 0;
        public void Fire()
        {
            if (Disposed) return;
            clock.Advance(DueTime);
            callback(state);
        }
        public bool Change(TimeSpan nextDueTime, TimeSpan nextPeriod)
        {
            DueTime = nextDueTime;
            Period = nextPeriod;
            return !Disposed;
        }
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
