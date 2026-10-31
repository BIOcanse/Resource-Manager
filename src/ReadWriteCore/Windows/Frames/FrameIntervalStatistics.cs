namespace ResourceManager.ReadWrite.Windows.Frames;

/// <summary>
/// 一段帧间隔的统计。叠加层和定向监控报告共用这一份算法。
/// 1% / 0.1% Low 取最慢的 ceil(N×1%) / ceil(N×0.1%) 个间隔求平均毫秒数再换算成 FPS，不是 1000 / P99。
/// 分位数用最近秩（nearest-rank）。计算本身就是 <see cref="FrameDurationStatistics"/>，这里只把毫秒换成 FPS。
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
        foreach (var interval in intervalsMs)
        {
            if (!double.IsFinite(interval) || interval <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(intervalsMs), "Frame intervals must be positive finite milliseconds.");
            }
        }

        var durations = FrameDurationStatistics.Compute(intervalsMs);
        return durations is null
            ? null
            : new FrameIntervalStatistics(
                durations.Count,
                durations.TotalMs,
                durations.Count * 1000d / durations.TotalMs,
                1000d / durations.OnePercentHighMs,
                1000d / durations.PointOnePercentHighMs,
                durations.P50Ms,
                durations.P95Ms,
                durations.P99Ms,
                durations.MaxMs);
    }
}

/// <summary>
/// 一段每帧耗时（CPU busy、CPU wait、GPU busy、显示时间等）的统计，与帧间隔同一份算法：
/// OnePercentHighMs / PointOnePercentHighMs 是最慢的 ceil(N×1%) / ceil(N×0.1%) 个值的平均毫秒数
/// （对帧间隔就是 1% / 0.1% Low 换算前的值）；分位数用最近秩。值可以为 0（例如 Present 调用内不等待）。
/// </summary>
public sealed record FrameDurationStatistics(
    int Count,
    double TotalMs,
    double AverageMs,
    double OnePercentHighMs,
    double PointOnePercentHighMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs)
{
    /// <summary>没有值时返回 null；值必须是非负的有限毫秒数。</summary>
    public static FrameDurationStatistics? Compute(ReadOnlySpan<double> durationsMs)
    {
        if (durationsMs.IsEmpty)
        {
            return null;
        }

        var sorted = durationsMs.ToArray();
        var total = 0d;
        foreach (var duration in sorted)
        {
            if (!double.IsFinite(duration) || duration < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(durationsMs), "Frame durations must be non-negative finite milliseconds.");
            }

            total += duration;
        }

        Array.Sort(sorted);
        var count = sorted.Length;
        return new FrameDurationStatistics(
            count,
            total,
            total / count,
            SlowestAverage(sorted, 100),
            SlowestAverage(sorted, 1000),
            NearestRank(sorted, 50, 100),
            NearestRank(sorted, 95, 100),
            NearestRank(sorted, 99, 100),
            sorted[^1]);
    }

    // 最慢的 ceil(N / divisor) 个值的平均；整数运算避免 N×0.01 的浮点误差。
    private static double SlowestAverage(double[] sorted, int divisor)
    {
        var slowest = (sorted.Length + divisor - 1) / divisor;
        var sum = 0d;
        for (var index = sorted.Length - slowest; index < sorted.Length; index++)
        {
            sum += sorted[index];
        }

        return sum / slowest;
    }

    private static double NearestRank(double[] sorted, int numerator, int denominator)
    {
        var rank = (sorted.Length * numerator + denominator - 1) / denominator;
        return sorted[Math.Max(rank, 1) - 1];
    }
}
