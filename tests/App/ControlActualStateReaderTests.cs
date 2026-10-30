using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlActualStateReaderTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FirstRoundPublishesWithoutAdvancingClockAndBufferKeepsRunForNextRequest()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        var first = reader.FetchOnceAsync("control-test:first", default);
        Assert.True(reader.ActualControlValuesInUse);
        await reader.StartAsync(default);
        var run = reader.ExecuteTask;
        Assert.NotNull(await first.WaitAsync(Deadline));
        Assert.Equal(DateTimeOffset.UnixEpoch, reader.Current.ReadAt);
        Assert.Equal(1, writer.Reads);
        Assert.True(reader.ActualControlValuesInUse);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, writer.Reads);
        writer.Read = _ => Task.FromResult<ControlActualValue?>(new("device", "power", 77));
        Assert.Equal(77, Assert.Single((await reader.FetchOnceAsync("control-test:second", default)
            .WaitAsync(Deadline))!.Values).Number);
        Assert.Same(run, reader.ExecuteTask);
        Assert.Equal(2, writer.Reads);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.True(reader.ActualControlValuesInUse);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(reader.ActualControlValuesInUse);
        Assert.Equal(2, writer.Reads);
        await reader.StopAsync(default);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    public async Task OneShotStopBufferExpiresExactlyAndSubscriptionsStillKeepSourceInUse(int milliseconds)
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock,
            stopBuffer: TimeSpan.FromMilliseconds(milliseconds));
        var changes = new List<bool>();
        reader.SubscriptionsChanged += () => changes.Add(reader.ActualControlValuesInUse);
        var once = reader.FetchOnceAsync("buffer-test", default);
        await reader.StartAsync(default);
        await once.WaitAsync(Deadline);
        Assert.Equal(milliseconds != 0, reader.ActualControlValuesInUse);
        if (milliseconds != 0)
        {
            using var lease = reader.AcquireSubscription(TenSeconds);
            await WaitUntilAsync(() => writer.Reads == 2);
            clock.Advance(TenSeconds);
            Assert.True(reader.ActualControlValuesInUse);
            lease.Dispose();
            Assert.False(reader.ActualControlValuesInUse);
        }
        Assert.Equal([true, false], changes);
        await reader.StopAsync(default);
    }

    [Fact]
    public async Task CompletedRequestBufferSurvivesStaleWorkerStopAndNewRequestExtendsDeadline()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        var first = reader.FetchOnceAsync("before-stop", default);
        await reader.StartAsync(default);
        await first.WaitAsync(Deadline);
        await reader.StopAsync(default); // A stop decision may already be in flight when a request completes.
        Assert.True(reader.ActualControlValuesInUse);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.True(reader.ActualControlValuesInUse);
        await reader.StartAsync(default);
        await reader.FetchOnceAsync("after-restart", default).WaitAsync(Deadline);
        Assert.Equal(2, writer.Reads);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(reader.ActualControlValuesInUse); // The original deadline cannot withdraw the extended buffer.
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(reader.ActualControlValuesInUse);
        Assert.Equal(2, writer.Reads);
        await reader.StopAsync(default);
    }

    [Fact]
    public async Task OwnerDisposalWithdrawsUnexpiredBufferAndDisposesItsDeadline()
    {
        var clock = new ControlledClock();
        var reader = new ControlActualStateReader(new Catalog(), [new CountingWriter()], clock);
        var changes = new List<bool>();
        reader.SubscriptionsChanged += () => changes.Add(reader.ActualControlValuesInUse);
        var once = reader.FetchOnceAsync("dispose-buffer", default);
        await reader.StartAsync(default);
        await once.WaitAsync(Deadline);
        Assert.True(reader.ActualControlValuesInUse);
        Assert.Equal(1, clock.ActiveTimers);
        reader.Dispose();
        Assert.False(reader.ActualControlValuesInUse);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Equal([true, false], changes);
        clock.Advance(TenSeconds);
        Assert.Equal([true, false], changes);
    }

    [Fact]
    public async Task SubscriptionReadsOnlyAtItsDueTime()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        using var lease = reader.AcquireSubscription(TenSeconds);
        await reader.StartAsync(default);
        await WaitUntilAsync(() => writer.Reads == 1 && reader.Current.Values.Count == 1);
        await clock.WaitForDueAsync(TenSeconds);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, writer.Reads);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => writer.Reads == 2);
        await clock.WaitForDueAsync(TimeSpan.FromSeconds(20));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(2, writer.Reads);
        await reader.StopAsync(default);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task TwoSubscriptionsUseEarliestDueAndRemovedFastSubscriptionLeavesSlowDeadline()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        using var fast = reader.AcquireSubscription(TimeSpan.FromSeconds(2));
        using var slow = reader.AcquireSubscription(TenSeconds);
        await reader.StartAsync(default);
        await WaitUntilAsync(() => writer.Reads == 1);
        for (var round = 1; round <= 3; round++)
        {
            await clock.WaitForDueAsync(TimeSpan.FromSeconds(round * 2));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(round, writer.Reads);
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => writer.Reads == round + 1);
        }
        fast.Dispose();
        fast.Dispose();
        Assert.True(reader.ActualControlValuesInUse);
        await clock.WaitForDueAsync(TenSeconds);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(4, writer.Reads);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => writer.Reads == 5 && reader.Current.ReadAt == DateTimeOffset.UnixEpoch.AddSeconds(10));
        var latest = reader.Current;
        slow.Dispose();
        Assert.False(reader.ActualControlValuesInUse);
        await reader.StopAsync(default);
        Assert.Equal(0, clock.ActiveTimers);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(5, writer.Reads);
        Assert.Same(latest, reader.Current);
    }

    [Fact]
    public async Task RequestsBeforeRoundShareItAndRequestDuringReadGetsOneFollowingRound()
    {
        var clock = new ControlledClock();
        var pending = new TaskCompletionSource<ControlActualValue?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new CountingWriter { Read = _ => pending.Task };
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock, stopBuffer: TimeSpan.Zero);
        var first = reader.FetchOnceAsync("first", default);
        var second = reader.FetchOnceAsync("second", default);
        await reader.StartAsync(default);
        await WaitUntilAsync(() => writer.Reads == 1);
        var third = reader.FetchOnceAsync("during-round", default);
        writer.Read = _ => Task.FromResult<ControlActualValue?>(new("device", "power", 77));
        pending.SetResult(new("device", "power", 42));
        var results = await Task.WhenAll(first, second, third).WaitAsync(Deadline);
        Assert.Same(results[0], results[1]);
        Assert.Equal(42, Assert.Single(results[0]!.Values).Number);
        Assert.Equal(77, Assert.Single(results[2]!.Values).Number);
        Assert.Equal(2, writer.Reads);
        Assert.False(reader.ActualControlValuesInUse);
        await reader.StopAsync(default);
    }

    [Fact]
    public async Task NoWorkDoesNotReadOrCreateCadenceAndSubscriptionAdmissionOnlyPublishesFacts()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        var catalog = new Catalog();
        using var reader = new ControlActualStateReader(catalog, [writer], clock);
        var changes = new List<bool>();
        reader.SubscriptionsChanged += () => changes.Add(reader.ActualControlValuesInUse);
        var lease = reader.AcquireSubscription(TenSeconds);
        Assert.True(reader.ActualControlValuesInUse);
        Assert.Equal(0, catalog.Reads);
        Assert.Equal(0, writer.Reads);
        Assert.Equal(0, writer.Probes);
        Assert.Equal(0, clock.CreatedTimers);
        lease.Dispose();
        lease.Dispose();
        await reader.StartAsync(default);
        await WaitUntilAsync(() => catalog.Discoveries == 1);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, writer.Reads);
        Assert.Equal(0, clock.CreatedTimers);
        Assert.Same(ControlActualState.Empty, reader.Current);
        Assert.False(reader.ActualControlValuesInUse);
        Assert.Equal([true, false], changes);
        await reader.StopAsync(default);
    }

    [Fact]
    public async Task LeasesRemainInUseAcrossWorkerRestartsAndFirstRoundReadsAgain()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        using var previous = reader.AcquireSubscription(TenSeconds);
        await reader.StartAsync(default);
        await WaitUntilAsync(() => reader.Current.Values.Count == 1);
        await reader.StopAsync(default);
        Assert.True(reader.ActualControlValuesInUse);
        var before = reader.Current;
        writer.Read = _ => Task.FromResult<ControlActualValue?>(new("device", "power", 77));
        using var current = reader.AcquireSubscription(TenSeconds);
        await reader.StartAsync(default);
        previous.Dispose();
        Assert.True(reader.ActualControlValuesInUse);
        await WaitUntilAsync(() => !ReferenceEquals(before, reader.Current));
        Assert.Equal(2, writer.Reads);
        Assert.Equal(77, Assert.Single(reader.Current.Values).Number);
        current.Dispose();
        Assert.False(reader.ActualControlValuesInUse);
        await reader.StopAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedEmptyOrFailedReadPublishesTypedUnreadableValue(bool fail)
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter();
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        using var lease = reader.AcquireSubscription(TenSeconds);
        await reader.StartAsync(default);
        await WaitUntilAsync(() => reader.Current.Values.Count == 1);
        var previous = reader.Current;
        await clock.WaitForDueAsync(TenSeconds);
        writer.Read = _ => fail
            ? Task.FromException<ControlActualValue?>(new IOException("Hardware read failed."))
            : Task.FromResult<ControlActualValue?>(null);
        clock.Advance(TenSeconds);
        await WaitUntilAsync(() => !ReferenceEquals(previous, reader.Current));
        var unreadable = Assert.Single(reader.Current.Values);
        Assert.Null(unreadable.Number);
        Assert.Equal("control-setting-read-unavailable", unreadable.UnreadableReason?.Code);
        Assert.True(reader.Current.ReadAt > previous.ReadAt);
        await reader.StopAsync(default);
    }

    [Fact]
    public async Task ControllerStopAfterLastUnsubscribeCancelsInFlightReadAndRejectsItsLateResult()
    {
        var clock = new ControlledClock();
        var pending = new TaskCompletionSource<ControlActualValue?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new CountingWriter { Read = _ => pending.Task };
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        var lease = reader.AcquireSubscription(TenSeconds);
        await reader.StartAsync(default);
        await WaitUntilAsync(() => writer.Reads == 1);
        lease.Dispose();
        Assert.False(reader.ActualControlValuesInUse);
        Assert.False(writer.LastToken.IsCancellationRequested);
        var stop = reader.StopAsync(default);
        Assert.True(writer.LastToken.IsCancellationRequested);
        pending.SetResult(new("device", "power", 90));
        await stop.WaitAsync(Deadline);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Same(ControlActualState.Empty, reader.Current);
        Assert.Equal(1, writer.Reads);
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.AcquireSubscription(TenSeconds));
    }

    [Fact]
    public async Task CancellationWithdrawsRequestAndResidentBufferSurvivesWorkerStopUntilExpiry()
    {
        var clock = new ControlledClock();
        var writer = new CountingWriter { Read = async token => { await Task.Delay(Timeout.Infinite, token); return null; } };
        using var reader = new ControlActualStateReader(new Catalog(), [writer], clock);
        using var cancellation = new CancellationTokenSource();
        var pending = reader.FetchOnceAsync("cancel", cancellation.Token);
        await reader.StartAsync(default);
        await WaitUntilAsync(() => writer.Reads == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(reader.ActualControlValuesInUse);
        await reader.StopAsync(default);
        Assert.True(reader.ActualControlValuesInUse);
        clock.Advance(TenSeconds);
        Assert.False(reader.ActualControlValuesInUse);
        Assert.Equal(0, clock.ActiveTimers);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class Catalog : IControlObjectCatalog
    {
        public int Reads, Discoveries;
        public bool HasBridge;
        public bool ActualReadingsUseHardwareBridge => HasBridge;
        public Task DiscoverActualReadingsAsync(CancellationToken token)
        { Interlocked.Increment(ref Discoveries); return Task.CompletedTask; }
        public Task<ControlObjectCatalog> ReadObjectsAsync(CancellationToken token)
            => throw new InvalidOperationException("Rounds must project current values without single fetch.");
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
        public int Probes, Reads;
        public CancellationToken LastToken;
        public Func<CancellationToken, Task<ControlActualValue?>> Read =
            _ => Task.FromResult<ControlActualValue?>(new("device", "power", 42));
        public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
        { Interlocked.Increment(ref Probes); return ControlWriteAvailability.Yes(); }
        public Task<ControlActualValue?> ReadAsync(ControlObject target, ControlCapability capability, CancellationToken token)
        { LastToken = token; Interlocked.Increment(ref Reads); return Read(token); }
        public Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability,
            ControlSetting setting, CancellationToken token) => throw new NotSupportedException();
    }

    internal sealed class ControlledClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ControlledTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public int CreatedTimers;
        public int ActiveTimers { get { lock (gate) return timers.Count(timer => timer.Active); } }
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        public void Advance(TimeSpan time)
        {
            List<ControlledTimer> due;
            lock (gate)
            {
                now += time;
                due = timers.Where(timer => timer.Active && timer.Due <= now).ToList();
                foreach (var timer in due) timer.Active = false;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        internal Task WaitForDueAsync(TimeSpan sinceEpoch)
            => WaitUntilAsync(() => { lock (gate) return timers.Any(timer => timer.Active
                && timer.Due == DateTimeOffset.UnixEpoch + sinceEpoch); });
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            lock (gate)
            {
                var timer = new ControlledTimer(this, callback, state);
                timers.Add(timer);
                CreatedTimers++;
                timer.Change(dueTime, period);
                return timer;
            }
        }
        private sealed class ControlledTimer(ControlledClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal bool Active;
            internal DateTimeOffset Due;
            private bool disposed;
            public bool Change(TimeSpan due, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (clock.gate)
                {
                    if (disposed) return false;
                    Active = due != Timeout.InfiniteTimeSpan;
                    if (Active) Due = clock.now + due;
                    return true;
                }
            }
            public void Dispose() { lock (clock.gate) { disposed = true; Active = false; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
