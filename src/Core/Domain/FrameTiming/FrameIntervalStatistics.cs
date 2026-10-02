namespace ResourceManager.App.Domain.FrameTiming;

/// <summary>
/// 一段帧间隔的统计。叠加层和定向监控报告共用这一份算法。
/// 1% / 0.1% Low 取最慢的 ceil(N×1%) / ceil(N×0.1%) 个间隔求平均毫秒数再换算成 FPS，不是 1000 / P99。
/// 分位数用最近秩（nearest-rank）。
/// </summary>
public sealed record FrameIntervalStatistics(
    int IntervalCount,
    double DurationMs,
    double AverageFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double P50FrameTimeMs,
    double P95FrameTimeMs,
    double P99FrameTimeMs,
    double MaxFrameTimeMs)
{
    /// <summary>没有间隔时返回 null；间隔必须是正的有限毫秒数。</summary>
    public static FrameIntervalStatistics? Compute(ReadOnlySpan<double> intervalsMs)
    {
        if (intervalsMs.IsEmpty)
        {
            return null;
        }

        var sorted = intervalsMs.ToArray();
        var total = 0d;
        foreach (var interval in sorted)
        {
            if (!double.IsFinite(interval) || interval <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(intervalsMs), "Frame intervals must be positive finite milliseconds.");
            }

            total += interval;
        }

        Array.Sort(sorted);
        var count = sorted.Length;
        return new FrameIntervalStatistics(
            count,
            total,
            count * 1000d / total,
            LowFps(sorted, 100),
            LowFps(sorted, 1000),
            NearestRank(sorted, 50, 100),
            NearestRank(sorted, 95, 100),
            NearestRank(sorted, 99, 100),
            sorted[^1]);
    }

    // 最慢的 ceil(N / divisor) 个间隔；整数运算避免 N×0.01 的浮点误差。
    private static double LowFps(double[] sorted, int divisor)
    {
        var slowest = (sorted.Length + divisor - 1) / divisor;
        var sum = 0d;
        for (var index = sorted.Length - slowest; index < sorted.Length; index++)
        {
            sum += sorted[index];
        }

        return 1000d / (sum / slowest);
    }

    private static double NearestRank(double[] sorted, int numerator, int denominator)
    {
        var rank = (sorted.Length * numerator + denominator - 1) / denominator;
        return sorted[Math.Max(rank, 1) - 1];
    }
}
