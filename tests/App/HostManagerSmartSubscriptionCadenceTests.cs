using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.ReadWrite.Publications;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerSmartSubscriptionCadenceTests
{
    private static readonly TimeSpan NormalInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EventInterval = TimeSpan.FromSeconds(5);
    private static readonly SchedulingProcessMetricMask ProcessMetrics = SchedulingProcessMetricMask.CpuUsage;

    [Fact]
    public async Task AllThreeSmartSubscriptionsTrackNormalEventNormalFrequency()
    {
        await using var fixture = new Fixture();

        await UpdateAllAsync(fixture.Scheduling, NormalInterval);
        await UpdateAllAsync(fixture.Scheduling, EventInterval);
        await UpdateAllAsync(fixture.Scheduling, NormalInterval);

        Assert.Equal(SmartRoundOutput.Residency, fixture.Scheduling.Driver);
        Assert.Equal([NormalInterval, EventInterval, NormalInterval], fixture.Residency.OutputIntervals);
        Assert.Equal([NormalInterval, EventInterval, NormalInterval], fixture.Process.ObservationIntervals);
        Assert.Equal([NormalInterval, EventInterval, NormalInterval], fixture.Hardware.ObservationIntervals);
    }

    [Theory]
    [InlineData(true, true, true, (int)SmartRoundOutput.Residency)]
    [InlineData(false, true, true, (int)SmartRoundOutput.Process)]
    [InlineData(false, false, true, (int)SmartRoundOutput.Hardware)]
    [InlineData(false, false, false, (int)SmartRoundOutput.None)]
    public async Task DriverUsesResidencyThenProcessThenHardwareThenNone(
        bool cpu,
        bool process,
        bool hardware,
        int expectedValue)
    {
        await using var fixture = new Fixture();
        var expected = (SmartRoundOutput)expectedValue;
        var request = hardware ? HardwareRequest() : EmptyHardwareRequest();

        await fixture.Scheduling.UpdateAsync(
            cpu,
            process ? ProcessMetrics : SchedulingProcessMetricMask.None,
            request,
            NormalInterval,
            CancellationToken.None);

        Assert.Equal(expected, fixture.Scheduling.Driver);
        Assert.Equal(expected != SmartRoundOutput.None, fixture.Scheduling.HasOutput);
        Assert.Equal(expected == SmartRoundOutput.Residency ? 1 : 0, fixture.Residency.OutputSubscriptionCount);
        Assert.Equal(expected == SmartRoundOutput.Process ? 1 : 0, fixture.Process.OutputSubscriptionCount);
        Assert.Equal(expected == SmartRoundOutput.Hardware ? 1 : 0, fixture.Hardware.OutputSubscriptionCount);
        Assert.Equal(process ? 1 : 0, fixture.Process.LeaseAcquisitionCount);
        Assert.Equal(hardware ? 1 : 0, fixture.Hardware.LeaseAcquisitionCount);
        Assert.Equal(
            process ? new[] { NormalInterval } : Array.Empty<TimeSpan>(),
            fixture.Process.ObservationIntervals);
        Assert.Equal(
            hardware ? new[] { NormalInterval } : Array.Empty<TimeSpan>(),
            fixture.Hardware.ObservationIntervals);
    }

    [Fact]
    public async Task NormalWaitHasNoPlannerTimerAndOnlyTheChosenOutputWakesIt()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new Fixture(clock);
        await UpdateAllAsync(fixture.Scheduling, NormalInterval);
        var deadline = new HostManagerVersionedWakeDeadline(clock);
        deadline.PublishScheduled(null);
        using var cancellation = new CancellationTokenSource();
        var wait = fixture.Scheduling.WaitAsync(deadline, cancellation.Token);

        try
        {
            Assert.False(wait.IsCompleted);
            Assert.Equal(0, clock.TimerCreationCount);
            Assert.Equal(1, fixture.Residency.OutputSubscriptionCount);
            Assert.Equal(0, fixture.Process.OutputSubscriptionCount);
            Assert.Equal(0, fixture.Hardware.OutputSubscriptionCount);

            fixture.Process.PublishOutput();
            fixture.Hardware.PublishOutput();
            await Task.Yield();
            Assert.False(wait.IsCompleted);

            fixture.Residency.PublishOutput();
            var result = await wait;

            Assert.False(result.HasDeadline);
            Assert.Equal(0, clock.TimerCreationCount);
            Assert.Equal(0, fixture.Residency.ReadCount);
            Assert.Equal(0, fixture.Process.ReadLatestCount);
            Assert.Equal(0, fixture.Hardware.ReadLatestCount);
            Assert.Equal(0, fixture.Process.OutputSubscriptionCount);
            Assert.Equal(0, fixture.Hardware.OutputSubscriptionCount);
        }
        finally
        {
            await CancelAndDrainAsync(wait, cancellation);
        }
    }

    [Fact]
    public async Task SharedFasterResidencyPublicationBeforeRequestedIntervalDoesNotCompleteRound()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new Fixture(clock);
        await fixture.Scheduling.UpdateAsync(
            cpu: true,
            process: ProcessMetrics,
            hardwareRequest: EmptyHardwareRequest(),
            interval: NormalInterval,
            cancellationToken: CancellationToken.None);
        fixture.Scheduling.RoundCompleted();
        _ = await clock.ReadTimestampAsync();
        var deadline = new HostManagerVersionedWakeDeadline(clock);
        deadline.PublishScheduled(null);
        using var cancellation = new CancellationTokenSource();
        var wait = fixture.Scheduling.WaitAsync(deadline, cancellation.Token);

        try
        {
            clock.Advance(EventInterval);
            fixture.Residency.PublishOutput();
            _ = await clock.ReadTimestampAsync();
            await Task.Yield();

            Assert.False(wait.IsCompleted);
            Assert.Equal(0, clock.TimerCreationCount);

            clock.Advance(NormalInterval - EventInterval);
            fixture.Residency.PublishOutput();
            var result = await wait;

            Assert.False(result.HasDeadline);
            Assert.Equal(0, clock.TimerCreationCount);
        }
        finally
        {
            await CancelAndDrainAsync(wait, cancellation);
        }
    }

    [Fact]
    public async Task EmptyResidencyPublicationWakesWithoutReadingLatestValues()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new Fixture(clock);
        await UpdateAllAsync(fixture.Scheduling, NormalInterval);
        var deadline = new HostManagerVersionedWakeDeadline(clock);
        deadline.PublishScheduled(null);
        using var cancellation = new CancellationTokenSource();
        var wait = fixture.Scheduling.WaitAsync(deadline, cancellation.Token);

        try
        {
            fixture.Residency.PublishOutput();
            var result = await wait;

            Assert.False(result.HasDeadline);
            Assert.Equal(0, fixture.Residency.ReadCount);
            Assert.Equal(0, fixture.Process.ReadLatestCount);
            Assert.Equal(0, fixture.Hardware.ReadLatestCount);
        }
        finally
        {
            await CancelAndDrainAsync(wait, cancellation);
        }
    }

    [Fact]
    public async Task ForegroundDeadlineWakesWhileChosenOutputIsStillPending()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new Fixture(clock);
        await fixture.Scheduling.UpdateAsync(
            cpu: true,
            process: SchedulingProcessMetricMask.None,
            hardwareRequest: EmptyHardwareRequest(),
            interval: NormalInterval,
            cancellationToken: CancellationToken.None);
        var deadline = new HostManagerVersionedWakeDeadline(clock);
        deadline.PublishScheduled(null);
        using var cancellation = new CancellationTokenSource();
        var wait = fixture.Scheduling.WaitAsync(deadline, cancellation.Token);

        try
        {
            Assert.Equal(1, fixture.Residency.ActiveOutputCount);
            var foreground = deadline.PublishForeground(TimeSpan.Zero);
            var result = await wait;

            Assert.Equal(foreground, result);
            Assert.True(result.HasDeadline);
            Assert.Equal(1, fixture.Residency.ActiveOutputCount);
            Assert.Equal(0, clock.TimerCreationCount);
        }
        finally
        {
            await CancelAndDrainAsync(wait, cancellation);
        }
    }

    [Theory]
    [InlineData(false, (int)SmartRoundOutput.Residency)]
    [InlineData(true, (int)SmartRoundOutput.Process)]
    public async Task WaitingRoundRemainsLiveAcrossFrequencyOrDriverReplacement(
        bool replaceDriver,
        int expectedDriverValue)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new Fixture(clock);
        var expectedDriver = (SmartRoundOutput)expectedDriverValue;
        await UpdateAllAsync(fixture.Scheduling, NormalInterval);
        var deadline = new HostManagerVersionedWakeDeadline(clock);
        deadline.PublishScheduled(null);
        using var cancellation = new CancellationTokenSource();
        var wait = fixture.Scheduling.WaitAsync(deadline, cancellation.Token);

        try
        {
            await fixture.Scheduling.UpdateAsync(
                cpu: !replaceDriver,
                process: ProcessMetrics,
                hardwareRequest: HardwareRequest(),
                interval: EventInterval,
                cancellationToken: CancellationToken.None);

            Assert.Equal(expectedDriver, fixture.Scheduling.Driver);
            if (replaceDriver)
            {
                Assert.Equal(1, fixture.Process.ActiveOutputCount);
                Assert.Equal(0, fixture.Residency.ActiveOutputCount);
                fixture.Process.PublishOutput();
            }
            else
            {
                Assert.Equal(1, fixture.Residency.ActiveOutputCount);
                fixture.Residency.PublishOutput();
            }

            var result = await wait;

            Assert.False(result.HasDeadline);
            Assert.Equal(0, fixture.Process.ReadLatestCount);
            Assert.Equal(0, fixture.Hardware.ReadLatestCount);
        }
        finally
        {
            await CancelAndDrainAsync(wait, cancellation);
        }
    }

    [Fact]
    public async Task NextOutputCadenceRemainsAnchoredWhenPreviousRoundTakesTime()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new Fixture(clock);
        await fixture.Scheduling.UpdateAsync(
            cpu: true,
            process: SchedulingProcessMetricMask.None,
            hardwareRequest: EmptyHardwareRequest(),
            interval: NormalInterval,
            cancellationToken: CancellationToken.None);
        var deadline = new HostManagerVersionedWakeDeadline(clock);
        deadline.PublishScheduled(null);
        using var cancellation = new CancellationTokenSource();
        var firstWait = fixture.Scheduling.WaitAsync(deadline, cancellation.Token);
        Task<HostManagerWakeDeadlineSnapshot>? nextWait = null;

        try
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            fixture.Residency.PublishOutput();
            Assert.False((await firstWait.WaitAsync(TimeSpan.FromSeconds(5))).HasDeadline);

            clock.Advance(TimeSpan.FromSeconds(1));
            fixture.Scheduling.RoundCompleted();
            nextWait = fixture.Scheduling.WaitAsync(deadline, cancellation.Token);

            clock.Advance(TimeSpan.FromSeconds(29));
            fixture.Residency.PublishOutput();
            var nextRound = await nextWait.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(nextRound.HasDeadline);
            Assert.Equal(0, clock.TimerCreationCount);
        }
        finally
        {
            await CancelAndDrainAsync(firstWait, cancellation);
            if (nextWait is not null)
            {
                await CancelAndDrainAsync(nextWait, cancellation);
            }
        }
    }

    [Fact]
    public async Task ShutdownDisposesChosenOutputAndAllRemainingObservationLeases()
    {
        await using var fixture = new Fixture();
        await UpdateAllAsync(fixture.Scheduling, NormalInterval);

        Assert.Equal(1, fixture.Residency.ActiveOutputCount);
        Assert.Equal(1, fixture.Process.ActiveLeaseCount);
        Assert.Equal(1, fixture.Hardware.ActiveLeaseCount);

        await fixture.Scheduling.DisposeAsync();

        Assert.Equal(0, fixture.Residency.ActiveOutputCount);
        Assert.Equal(1, fixture.Residency.DisposedOutputCount);
        Assert.Equal(0, fixture.Process.ActiveLeaseCount);
        Assert.Equal(1, fixture.Process.DisposedLeaseCount);
        Assert.Equal(0, fixture.Hardware.ActiveLeaseCount);
        Assert.Equal(1, fixture.Hardware.DisposedLeaseCount);
    }

    private static Task UpdateAllAsync(HostManagerSchedulingSubscriptions scheduling, TimeSpan interval)
        => scheduling.UpdateAsync(true, ProcessMetrics, HardwareRequest(), interval, CancellationToken.None);

    private static MetricSampleRequest HardwareRequest()
        => MetricSampleRequest.ForIds(["test.hardware.metric"]);

    private static MetricSampleRequest EmptyHardwareRequest()
        => MetricSampleRequest.ForIds(Array.Empty<string>());

    private static async Task CancelAndDrainAsync(
        Task<HostManagerWakeDeadlineSnapshot> wait,
        CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        try
        {
            _ = await wait;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal Fixture(TimeProvider? clock = null)
        {
            Scheduling = new HostManagerSchedulingSubscriptions(Residency, Process, Hardware, clock);
        }

        internal FakeResidency Residency { get; } = new();
        internal FakeProcessObservation Process { get; } = new();
        internal FakeHardwareObservation Hardware { get; } = new();
        internal HostManagerSchedulingSubscriptions Scheduling { get; }

        public ValueTask DisposeAsync() => Scheduling.DisposeAsync();
    }

    private sealed class FakeResidency : ICpuCoreResidencyReader
    {
        private readonly ConcurrentQueue<TimeSpan> outputIntervals = new();
        private int outputSubscriptionCount;
        private int activeOutputCount;
        private int disposedOutputCount;
        private int readCount;

        internal CurrentValuePublicationSignal Publications { get; } = new();
        internal TimeSpan[] OutputIntervals => outputIntervals.ToArray();
        internal int OutputSubscriptionCount => Volatile.Read(ref outputSubscriptionCount);
        internal int ActiveOutputCount => Volatile.Read(ref activeOutputCount);
        internal int DisposedOutputCount => Volatile.Read(ref disposedOutputCount);
        internal int ReadCount => Volatile.Read(ref readCount);

        public CpuCoreResidencySnapshot? Read()
        {
            Interlocked.Increment(ref readCount);
            return null;
        }

        public IDisposable AcquireSubscription(string subscriptionId, TimeSpan refreshInterval)
            => new TrackedLease(static () => { });

        public async IAsyncEnumerable<CpuCoreResidencySnapshot?> SubscribeAsync(
            string subscriptionId,
            TimeSpan interval,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            outputIntervals.Enqueue(interval);
            Interlocked.Increment(ref outputSubscriptionCount);
            Interlocked.Increment(ref activeOutputCount);
            using var subscription = Publications.Subscribe();
            try
            {
                while (true)
                {
                    await subscription.WaitAsync(cancellationToken);
                    yield return null;
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeOutputCount);
                Interlocked.Increment(ref disposedOutputCount);
            }
        }

        internal void PublishOutput() => Publications.Publish();
    }

    private sealed class FakeProcessObservation : ISchedulingProcessFactObservationSource, IResourceBreakdownPushSource
    {
        private const string OutputDataset = "test.process.output";
        private readonly ConcurrentQueue<TimeSpan> observationIntervals = new();
        private int outputSubscriptionCount;
        private int activeOutputCount;
        private int disposedOutputCount;
        private int leaseAcquisitionCount;
        private int activeLeaseCount;
        private int disposedLeaseCount;
        private int readLatestCount;

        internal SamplingPublicationSignal Publications { get; } = new();
        internal TimeSpan[] ObservationIntervals => observationIntervals.ToArray();
        internal int OutputSubscriptionCount => Volatile.Read(ref outputSubscriptionCount);
        internal int ActiveOutputCount => Volatile.Read(ref activeOutputCount);
        internal int DisposedOutputCount => Volatile.Read(ref disposedOutputCount);
        internal int LeaseAcquisitionCount => Volatile.Read(ref leaseAcquisitionCount);
        internal int ActiveLeaseCount => Volatile.Read(ref activeLeaseCount);
        internal int DisposedLeaseCount => Volatile.Read(ref disposedLeaseCount);
        internal int ReadLatestCount => Volatile.Read(ref readLatestCount);

        public IDisposable AcquireSubscription(
            string subscriptionId,
            SchedulingProcessMetricMask metricMask,
            TimeSpan refreshInterval)
        {
            observationIntervals.Enqueue(refreshInterval);
            Interlocked.Increment(ref leaseAcquisitionCount);
            Interlocked.Increment(ref activeLeaseCount);
            return new TrackedLease(() =>
            {
                Interlocked.Decrement(ref activeLeaseCount);
                Interlocked.Increment(ref disposedLeaseCount);
            });
        }

        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request)
        {
            Interlocked.Increment(ref readLatestCount);
            return null;
        }

        public async IAsyncEnumerable<ResourceBreakdownSnapshot> SubscribeAsync(
            string subscriptionId,
            ResourceBreakdownSampleRequest request,
            TimeSpan interval,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref outputSubscriptionCount);
            Interlocked.Increment(ref activeOutputCount);
            try
            {
                using var demand = AcquireSubscription(subscriptionId, request.SchedulingMetricMask, interval);
                using var subscription = Publications.Subscribe([OutputDataset]);
                while (true)
                {
                    await subscription.WaitAsync(cancellationToken);
                    yield return null!;
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeOutputCount);
                Interlocked.Increment(ref disposedOutputCount);
            }
        }

        internal void PublishOutput() => Publications.Publish([OutputDataset]);
    }

    private sealed class FakeHardwareObservation : IMetricSnapshotObservationSource, IMetricSnapshotPushSource
    {
        private const string OutputDataset = "test.hardware.output";
        private readonly ConcurrentQueue<TimeSpan> observationIntervals = new();
        private int outputSubscriptionCount;
        private int activeOutputCount;
        private int disposedOutputCount;
        private int leaseAcquisitionCount;
        private int activeLeaseCount;
        private int disposedLeaseCount;
        private int readLatestCount;

        internal SamplingPublicationSignal Publications { get; } = new();
        internal TimeSpan[] ObservationIntervals => observationIntervals.ToArray();
        internal int OutputSubscriptionCount => Volatile.Read(ref outputSubscriptionCount);
        internal int ActiveOutputCount => Volatile.Read(ref activeOutputCount);
        internal int DisposedOutputCount => Volatile.Read(ref disposedOutputCount);
        internal int LeaseAcquisitionCount => Volatile.Read(ref leaseAcquisitionCount);
        internal int ActiveLeaseCount => Volatile.Read(ref activeLeaseCount);
        internal int DisposedLeaseCount => Volatile.Read(ref disposedLeaseCount);
        internal int ReadLatestCount => Volatile.Read(ref readLatestCount);

        public IDisposable AcquireSubscription(
            string subscriptionId,
            MetricSampleRequest request,
            TimeSpan refreshInterval)
        {
            observationIntervals.Enqueue(refreshInterval);
            Interlocked.Increment(ref leaseAcquisitionCount);
            Interlocked.Increment(ref activeLeaseCount);
            return new TrackedLease(() =>
            {
                Interlocked.Decrement(ref activeLeaseCount);
                Interlocked.Increment(ref disposedLeaseCount);
            });
        }

        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request)
        {
            Interlocked.Increment(ref readLatestCount);
            return null;
        }

        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(
            string subscriptionId,
            MetricSampleRequest request,
            TimeSpan interval,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref outputSubscriptionCount);
            Interlocked.Increment(ref activeOutputCount);
            try
            {
                using var demand = AcquireSubscription(subscriptionId, request, interval);
                using var subscription = Publications.Subscribe([OutputDataset]);
                while (true)
                {
                    await subscription.WaitAsync(cancellationToken);
                    yield return null!;
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeOutputCount);
                Interlocked.Increment(ref disposedOutputCount);
            }
        }

        internal void PublishOutput() => Publications.Publish([OutputDataset]);
    }

    private sealed class TrackedLease(Action dispose) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                dispose();
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly Channel<long> timestampReads = Channel.CreateUnbounded<long>();
        private long timestamp;
        private int timerCreationCount;

        internal int TimerCreationCount => Volatile.Read(ref timerCreationCount);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            var current = Volatile.Read(ref timestamp);
            timestampReads.Writer.TryWrite(current);
            return current;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref timerCreationCount);
            throw new InvalidOperationException("The Smart cadence tests must not create clock timers.");
        }

        internal void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            }
            Interlocked.Add(ref timestamp, elapsed.Ticks);
        }

        internal ValueTask<long> ReadTimestampAsync(CancellationToken cancellationToken = default)
            => timestampReads.Reader.ReadAsync(cancellationToken);
    }
}
