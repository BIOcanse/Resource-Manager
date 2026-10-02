using ResourceManager.App.Domain.FrameTiming;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class PresentFrameLedgerTests
{
    private static readonly long Start = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long TenMs = TimeSpan.FromMilliseconds(10).Ticks;

    [Fact]
    public void RuntimeStreamsReplaceTheGraphicsKernelCopyOfTheSameFrames()
    {
        var ledger = new PresentFrameLedger(_ => 111);
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
        var ledger = new PresentFrameLedger(_ => 222);
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
        var ledger = new PresentFrameLedger(_ => 333);
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
        var ledger = new PresentFrameLedger(_ => null);
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
        var ledger = new PresentFrameLedger(_ => { reads++; return null; });
        for (var frame = 0; frame < 10; frame++)
        {
            ledger.Present(50, FramePresentSource.Dxgi, 1, Start + frame * TenMs);
        }

        Assert.Empty(ledger.Read(Start + 10 * TenMs, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void OnlyTheRequestedWindowIsMeasuredAndOldStreamsArePruned()
    {
        var ledger = new PresentFrameLedger(_ => 666);
        for (var frame = 0; frame < 200; frame++)
        {
            ledger.Present(60, FramePresentSource.Dxgi, 1, Start + frame * TenMs);
        }

        var now = Start + 200 * TenMs;
        var stream = Assert.Single(Assert.Single(ledger.Read(now, TimeSpan.FromMilliseconds(500))).Streams);
        Assert.Equal(49, stream.Statistics.IntervalCount);

        Assert.Empty(ledger.Read(now + PresentFrameLedger.Retention.Ticks + TenMs, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, ledger.StreamCount);
    }

    [Fact]
    public void OutOfOrderArrivalIsSortedBeforeMeasuring()
    {
        var ledger = new PresentFrameLedger(_ => 777);
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
        var ledger = new PresentFrameLedger(processId => processId);
        for (var frame = 0; frame < PresentFrameLedger.MaximumTimestampsPerStream + 10; frame++)
        {
            ledger.Present(80, FramePresentSource.Dxgi, 1, Start + frame * TimeSpan.FromMilliseconds(1).Ticks);
        }

        var now = Start + (PresentFrameLedger.MaximumTimestampsPerStream + 10) * TimeSpan.FromMilliseconds(1).Ticks;
        var stream = Assert.Single(Assert.Single(ledger.Read(now, TimeSpan.FromSeconds(29))).Streams);
        Assert.Equal(PresentFrameLedger.MaximumTimestampsPerStream - 1, stream.Statistics.IntervalCount);

        for (var processId = 1000; processId < 1000 + PresentFrameLedger.MaximumStreams + 5; processId++)
        {
            ledger.Present(processId, FramePresentSource.Dxgi, 1, now);
        }

        Assert.Equal(PresentFrameLedger.MaximumStreams, ledger.StreamCount);
    }
}
