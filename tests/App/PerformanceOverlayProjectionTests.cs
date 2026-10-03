using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Domain.FrameTiming;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlayProjectionTests
{
    [Fact]
    public void EnabledRunningSoftwareIncludesEveryProcessAndExcludesDisabledOrAbsentSoftware()
    {
        var settings = new[]
        {
            PerformanceOverlaySettings.Default("game") with { Enabled = true },
            PerformanceOverlaySettings.Default("other"),
            PerformanceOverlaySettings.Default("absent") with { Enabled = true }
        };
        var facts = Snapshot(
            Process(10, 100, "game"), Process(11, 101, "game"),
            Process(12, 102, "other"));

        var selected = PerformanceOverlayProjection.SelectTargets(settings, facts);

        Assert.Equal([10, 11], selected.Select(static item => item.ProcessId));
        Assert.Empty(PerformanceOverlayProjection.SelectTargets(settings, null));
    }

    [Fact]
    public void ReusedPidCannotBorrowOldFrameOrResourceValues()
    {
        var identity = Process(10, 200, "game");
        var old = Process(10, 100, "game") with
        {
            ValidMetricMask = SchedulingProcessMetricMask.CpuUsage,
            CpuUsagePercent = 90
        };
        var facts = Snapshot(identity) with { Processes = [old] };
        var stats = FrameIntervalStatistics.Compute([10d, 10d])!;
        var frames = new FrameTimingSnapshot(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), true,
            [new FrameTimingProcess(10, 100,
                [new FrameTimingStream(FramePresentSource.Dxgi, 1, DateTimeOffset.UtcNow, stats)])]);
        var breakdown = new ResourceBreakdownSnapshot(DateTimeOffset.UtcNow,
            [new ResourceBreakdownBar("memory.usage", "Memory", "B", "active", 1000, 1000, 100,
                [new ResourceSoftwareSegment("game", "Game", "Game", "Game", 500, 50, 1,
                    [new ResourceProcessSegment(10, "Game", null, 500, 50, 100) { ProcessStartKey = 100 }])])]);
        var setting = PerformanceOverlaySettings.Default("game") with
        {
            Enabled = true,
            Metrics = [PerformanceOverlayMetricIds.Fps, PerformanceOverlayMetricIds.Cpu,
                PerformanceOverlayMetricIds.Memory]
        };

        var result = PerformanceOverlayProjection.Create([setting], facts, breakdown, null, frames,
            DateTimeOffset.UtcNow);

        var target = Assert.Single(result.Targets);
        Assert.Equal((ulong)200, target.ProcessStartKey);
        Assert.Empty(target.FrameStreams);
        Assert.All(target.Metrics, item => Assert.Null(item.Value));
    }

    [Fact]
    public void MostPresentsStreamSuppliesFrameMetricsAndAllStreamsRemainVisible()
    {
        var statsShort = FrameIntervalStatistics.Compute([20d, 20d])!;
        var statsLong = FrameIntervalStatistics.Compute(Enumerable.Repeat(10d, 9).ToArray())!;
        var frames = new FrameTimingSnapshot(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), true,
            [new FrameTimingProcess(10, 200,
            [
                new FrameTimingStream(FramePresentSource.Dxgi, 1, DateTimeOffset.UtcNow, statsShort) { PresentCount = 12 },
                new FrameTimingStream(FramePresentSource.Dxgi, 2, DateTimeOffset.UtcNow, statsLong) { PresentCount = 10 }
            ])]);
        var setting = PerformanceOverlaySettings.Default("game") with
        {
            Enabled = true,
            Metrics = [PerformanceOverlayMetricIds.Fps, PerformanceOverlayMetricIds.FrameTime,
                PerformanceOverlayMetricIds.OnePercentLow]
        };

        var result = PerformanceOverlayProjection.Create([setting], Snapshot(Process(10, 200, "game")),
            null, null, frames, DateTimeOffset.UtcNow);

        var target = Assert.Single(result.Targets);
        Assert.Equal(2, target.FrameStreams.Count);
        Assert.Equal((ulong)1, Assert.Single(target.FrameStreams, static item => item.Selected).SwapChain);
        Assert.Equal(12, target.FrameStreams[0].PresentCount);
        Assert.Equal(50, target.Metrics[0].Value);
        Assert.Equal(20, target.Metrics[1].Value);
        Assert.Equal(50, target.Metrics[2].Value);
        Assert.Equal(9, target.Metrics[0].Labels.Count);
    }

    private static SchedulingProcessFact Process(int pid, ulong startKey, string softwareId) =>
        new(pid, startKey, $"Process {pid}", null, softwareId, softwareId, "Game", "Game",
            0, SchedulingProcessMetricMask.None, 0, 0, 1, []);

    private static SchedulingProcessFactSnapshot Snapshot(params SchedulingProcessFact[] attributed) =>
        new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks,
            (uint)attributed.Length, (uint)attributed.Length, 0, 0,
            SchedulingProcessMetricMask.CpuUsage, SchedulingProcessMetricMask.CpuUsage,
            attributed)
        {
            HasSeparateFoundationPayloads = true,
            AttributionProcesses = attributed
        };
}
