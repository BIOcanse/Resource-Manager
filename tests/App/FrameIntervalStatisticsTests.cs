using ResourceManager.App.Domain.FrameTiming;

namespace Resource_Manager_APP.Tests;

public sealed class FrameIntervalStatisticsTests
{
    [Fact]
    public void SteadyIntervalsGiveTheSameRateEverywhere()
    {
        var statistics = FrameIntervalStatistics.Compute(Enumerable.Repeat(10d, 1000).ToArray())!;

        Assert.Equal(1000, statistics.IntervalCount);
        Assert.Equal(100, statistics.AverageFps, 9);
        Assert.Equal(100, statistics.OnePercentLowFps, 9);
        Assert.Equal(100, statistics.PointOnePercentLowFps, 9);
        Assert.Equal(10, statistics.P50FrameTimeMs);
        Assert.Equal(10, statistics.P99FrameTimeMs);
        Assert.Equal(10, statistics.MaxFrameTimeMs);
    }

    [Fact]
    public void LowsAverageTheSlowestIntervalsInsteadOfInvertingAPercentile()
    {
        // 990 个 10 ms 加 10 个 50 ms：最慢的 ceil(1000×1%) = 10 个都是 50 ms。
        var intervals = Enumerable.Repeat(10d, 990).Concat(Enumerable.Repeat(50d, 10)).ToArray();

        var statistics = FrameIntervalStatistics.Compute(intervals)!;

        Assert.Equal(20, statistics.OnePercentLowFps, 9);
        Assert.Equal(20, statistics.PointOnePercentLowFps, 9);
        Assert.Equal(10, statistics.P99FrameTimeMs);
        Assert.NotEqual(1000 / statistics.P99FrameTimeMs, statistics.OnePercentLowFps);
        Assert.Equal(1000d * 1000 / (990 * 10 + 10 * 50), statistics.AverageFps, 9);
    }

    [Fact]
    public void SlowestCountUsesIntegerCeilingWithoutFloatingPointDrift()
    {
        // N = 300 时 ceil(N×1%) 必须是 3；用 300×0.01 的浮点结果会多算成 4 个。
        var intervals = Enumerable.Repeat(10d, 297).Concat(Enumerable.Repeat(40d, 3)).ToArray();

        var statistics = FrameIntervalStatistics.Compute(intervals)!;

        Assert.Equal(25, statistics.OnePercentLowFps, 9);
    }

    [Fact]
    public void SmallSamplesUseAtLeastTheSlowestInterval()
    {
        var statistics = FrameIntervalStatistics.Compute([10, 12, 30])!;

        Assert.Equal(1000d / 30, statistics.OnePercentLowFps, 9);
        Assert.Equal(1000d / 30, statistics.PointOnePercentLowFps, 9);
        Assert.Equal(12, statistics.P50FrameTimeMs);
        Assert.Equal(30, statistics.MaxFrameTimeMs);
    }

    [Fact]
    public void EmptyInputHasNoStatisticsAndInvalidIntervalsAreRejected()
    {
        Assert.Null(FrameIntervalStatistics.Compute([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameIntervalStatistics.Compute([10, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameIntervalStatistics.Compute([10, double.NaN]));
    }
}
