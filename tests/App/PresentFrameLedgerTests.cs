using ResourceManager.ReadWrite.Windows.Frames;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class PresentFrameLedgerTests
{
    // The pre-freedom-point bounds (16384 Presents per stream, 256 streams, 30 s), so eviction stays reachable in these tests.
    private static readonly PresentFrameLedgerLimits Limits = new(TimeSpan.FromSeconds(30), 16384, 256);

    [Fact]
    public void LossRechecksReusedPidWithoutDestroyingRecordedExactHistory()
    {
        long birth = 111;
        var reads = 0;
        var ledger = new PresentFrameLedger(_ => { reads++; return birth; }, Limits);
        ledger.Present(10, FramePresentSource.Dxgi, 1, Start, 7, 100, 1000);
        ledger.Present(10, FramePresentSource.Dxgi, 1, Start + TenMs, 7, 110, 1000);
        ledger.LegacyFlip(10, 7, true, 111);
        ledger.QueuePacketStart(10, 7, 9, 1, 3, true, 112);
        birth = 222;
        ledger.EventsLost();
        ledger.MmioFlip(9, Start + 2 * TenMs, 120, true);
        ledger.Present(10, FramePresentSource.Dxgi, 1, Start + 3 * TenMs);
        ledger.Present(10, FramePresentSource.Dxgi, 1, Start + 4 * TenMs);

        Assert.Equal(2, reads);
        Assert.Equal(222, Assert.Single(ledger.Read(Start + 5 * TenMs, TimeSpan.FromSeconds(1))).ProcessStartKey);
        var intervals = ledger.ReadIntervals(Start - 1, Start + 5 * TenMs);
        Assert.Contains(intervals, sample => sample.ProcessStartKey == 111);
        Assert.Contains(intervals, sample => sample.ProcessStartKey == 222);
        Assert.DoesNotContain(ledger.ReadDisplayStreams(Start + 5 * TenMs, TimeSpan.FromSeconds(1))
            .SelectMany(stream => stream.Frames), frame => frame.Outcome == FrameTimingDisplayOutcome.Displayed);
    }

    [Fact]
    public void TimestampEvictionMakesDelayedCursorIncompleteAndLaterWindowsRecover()
    {
        var ledger = new PresentFrameLedger(_ => 111, Limits);
        var oneMs = TimeSpan.TicksPerMillisecond;
        for (var index = 0; index < 20_000; index++)
            ledger.Present(10, FramePresentSource.Dxgi, 1, Start + index * oneMs);

        Assert.False(ledger.IsComplete(Start));
        Assert.Equal(Limits.PresentsPerStream - 1,
            ledger.ReadIntervals(Start, Start + 20_000 * oneMs).Count);
        Assert.True(ledger.IsComplete(Start + 4000 * oneMs));
        ledger.ProcessStopped(10);
        Assert.False(ledger.IsComplete(Start));
    }

    private static readonly long Start = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long TenMs = TimeSpan.FromMilliseconds(10).Ticks;

    [Fact]
    public void RuntimeStreamsReplaceTheGraphicsKernelCopyOfTheSameFrames()
    {
        var ledger = new PresentFrameLedger(_ => 111, Limits);
        for (var frame = 0; frame < 100; frame++)
        {
            ledger.Present(10, FramePresentSource.Dxgi, 0xA0, Start + frame * TenMs);
            ledger.Present(10, FramePresentSource.GraphicsKernel, 0, Start + frame * TenMs + 1000);
        }

        var process = Assert.Single(ledger.Read(Start + 100 * TenMs, TimeSpan.FromSeconds(5)));

        var stream = Assert.Single(process.Streams);
        Assert.Equal(FramePresentSource.Dxgi, stream.Source);
        Assert.Equal(0xA0UL, stream.SwapChain);
        Assert.Equal(100, stream.Statistics.AverageFps, 6);
    }

    [Fact]
    public void ProcessesWithoutRuntimeEventsFallBackToTheGraphicsKernel()
    {
        var ledger = new PresentFrameLedger(_ => 222, Limits);
        for (var frame = 0; frame < 50; frame++)
        {
            ledger.Present(20, FramePresentSource.GraphicsKernel, 0, Start + frame * TenMs);
        }

        var stream = Assert.Single(Assert.Single(ledger.Read(Start + 50 * TenMs, TimeSpan.FromSeconds(5))).Streams);
        Assert.Equal(FramePresentSource.GraphicsKernel, stream.Source);
        Assert.Equal(49, stream.Statistics.IntervalCount);
    }

    [Fact]
    public void SwapChainsAreReportedSeparately()
    {
        var ledger = new PresentFrameLedger(_ => 333, Limits);
        for (var frame = 0; frame < 20; frame++)
        {
            ledger.Present(30, FramePresentSource.Dxgi, 1, Start + frame * TenMs);
            ledger.Present(30, FramePresentSource.Dxgi, 2, Start + frame * 2 * TenMs);
        }

        var process = Assert.Single(ledger.Read(Start + 40 * TenMs, TimeSpan.FromSeconds(5)));

        Assert.Equal([1UL, 2UL], process.Streams.Select(stream => stream.SwapChain).Order());
    }

    [Fact]
    public void AReusedProcessIdStartsAFreshInstance()
    {
        var ledger = new PresentFrameLedger(_ => null, Limits);
        ledger.ProcessStarted(40, 1000);
        ledger.Present(40, FramePresentSource.Dxgi, 1, Start);
        ledger.Present(40, FramePresentSource.Dxgi, 1, Start + TenMs);
        ledger.ProcessStopped(40);
        ledger.ProcessStarted(40, 2000);
        ledger.Present(40, FramePresentSource.Dxgi, 1, Start + 2 * TenMs);
        ledger.Present(40, FramePresentSource.Dxgi, 1, Start + 3 * TenMs);

        var process = Assert.Single(ledger.Read(Start + 4 * TenMs, TimeSpan.FromSeconds(5)));

        Assert.Equal(2000, process.ProcessStartKey);
        Assert.Equal(1, Assert.Single(process.Streams).Statistics.IntervalCount);
    }

    [Fact]
    public void FramesFromProcessesWithoutAKnownStartTimeAreDropped()
    {
        var reads = 0;
        var ledger = new PresentFrameLedger(_ => { reads++; return null; }, Limits);
        for (var frame = 0; frame < 10; frame++)
        {
            ledger.Present(50, FramePresentSource.Dxgi, 1, Start + frame * TenMs);
        }

        Assert.Empty(ledger.Read(Start + 10 * TenMs, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void OnePresentExposesItsExactTimestampWithoutInventingAnInterval()
    {
        const long processBirth = 987654321;
        const ulong swapChain = 0xCAFE;
        var ledger = new PresentFrameLedger(_ => processBirth, Limits);
        var presentAt = Start + TenMs;
        ledger.Present(51, FramePresentSource.Dxgi, swapChain, presentAt);
        ledger.Present(51, FramePresentSource.Dxgi, swapChain, presentAt + TenMs * 2);

        var lastPresent = Assert.Single(ledger.ReadLastPresents(presentAt + TenMs, TimeSpan.FromSeconds(1)));

        Assert.Equal(51, lastPresent.ProcessId);
        Assert.Equal(processBirth, lastPresent.ProcessStartKey);
        Assert.Equal(FramePresentSource.Dxgi, lastPresent.Source);
        Assert.Equal(swapChain, lastPresent.SwapChain);
        Assert.Equal(new DateTimeOffset(presentAt, TimeSpan.Zero), lastPresent.LastPresentAt);
        Assert.Empty(ledger.Read(presentAt + TenMs, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void OnlyTheRequestedWindowIsMeasuredAndOldStreamsArePruned()
    {
        var ledger = new PresentFrameLedger(_ => 666, Limits);
        for (var frame = 0; frame < 200; frame++)
        {
            ledger.Present(60, FramePresentSource.Dxgi, 1, Start + frame * TenMs);
        }

        var now = Start + 200 * TenMs;
        var stream = Assert.Single(Assert.Single(ledger.Read(now, TimeSpan.FromMilliseconds(500))).Streams);
        Assert.Equal(49, stream.Statistics.IntervalCount);

        Assert.Empty(ledger.Read(now + Limits.Retention.Ticks + TenMs, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, ledger.StreamCount);
    }

    [Fact]
    public void OutOfOrderArrivalIsSortedBeforeMeasuring()
    {
        var ledger = new PresentFrameLedger(_ => 777, Limits);
        foreach (var frame in new[] { 0, 2, 1, 3, 5, 4 })
        {
            ledger.Present(70, FramePresentSource.Dxgi, 1, Start + frame * TenMs);
        }

        var statistics = Assert.Single(Assert.Single(ledger.Read(Start + 6 * TenMs, TimeSpan.FromSeconds(5))).Streams).Statistics;

        Assert.Equal(5, statistics.IntervalCount);
        Assert.Equal(10, statistics.MaxFrameTimeMs, 6);
    }

    [Fact]
    public void BuffersAreBoundedPerStreamAndInTotal()
    {
        var ledger = new PresentFrameLedger(processId => processId, Limits);
        for (var frame = 0; frame < Limits.PresentsPerStream + 10; frame++)
        {
            ledger.Present(80, FramePresentSource.Dxgi, 1, Start + frame * TimeSpan.FromMilliseconds(1).Ticks);
        }

        var now = Start + (Limits.PresentsPerStream + 10) * TimeSpan.FromMilliseconds(1).Ticks;
        var stream = Assert.Single(Assert.Single(ledger.Read(now, TimeSpan.FromSeconds(29))).Streams);
        Assert.Equal(Limits.PresentsPerStream - 1, stream.Statistics.IntervalCount);

        for (var processId = 1000; processId < 1000 + Limits.MaximumStreams + 5; processId++)
        {
            ledger.Present(processId, FramePresentSource.Dxgi, 1, now);
        }

        Assert.Equal(Limits.MaximumStreams, ledger.StreamCount);
    }

    [Fact]
    public void CursorReadsEveryIntervalOnceAndKeepsStoppedProcessTail()
    {
        var ledger = new PresentFrameLedger(_ => 1234, Limits);
        ledger.Present(90, FramePresentSource.Dxgi, 1, Start);
        ledger.Present(90, FramePresentSource.Dxgi, 1, Start + TenMs);
        ledger.Present(90, FramePresentSource.Dxgi, 1, Start + 2 * TenMs);

        var first = ledger.ReadIntervals(Start, Start + TenMs);
        Assert.Single(first);
        Assert.Equal(10, first[0].DurationMs);
        Assert.Single(ledger.ReadIntervals(Start + TenMs, Start + 2 * TenMs));

        ledger.ProcessStopped(90);
        Assert.Empty(ledger.Read(Start + 3 * TenMs, TimeSpan.FromSeconds(1)));
        var tail = ledger.ReadIntervals(Start + TenMs, Start + 3 * TenMs);
        Assert.Single(tail);
        Assert.Equal(1234, tail[0].ProcessStartKey);
    }
}
