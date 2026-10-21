using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed record WindowsDiskPerformanceCounters(
    long BytesRead,
    long BytesWritten,
    long ReadTime,
    long WriteTime,
    long IdleTime,
    uint ReadCount,
    uint WriteCount,
    uint QueueDepth,
    string StorageManagerName,
    long QueryTime);

/// <summary>One read-only disk performance result projected from a single monotonic sample window.</summary>
public sealed record WindowsDiskPerformanceMetrics(
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    double ReadIops,
    double WriteIops,
    double ActivePercent,
    double QueueLength,
    double? LatencyMilliseconds)
{
    /// <summary>Wall-clock observation instant from the end sample's native QueryTime; interval arithmetic uses monotonic timestamps instead.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    internal double? ValueFor(MetricFieldCode field) => field switch
    {
        MetricFieldCode.PhysicalDiskReadBytesPerSecond => ReadBytesPerSecond,
        MetricFieldCode.PhysicalDiskWriteBytesPerSecond => WriteBytesPerSecond,
        MetricFieldCode.PhysicalDiskReadIops => ReadIops,
        MetricFieldCode.PhysicalDiskWriteIops => WriteIops,
        MetricFieldCode.PhysicalDiskActivePercent => ActivePercent,
        MetricFieldCode.PhysicalDiskQueueLength => QueueLength,
        MetricFieldCode.PhysicalDiskLatency => LatencyMilliseconds,
        _ => null
    };
}

/// <summary>A selected storage-catalog identity for an explicit disk-performance capture.</summary>
public readonly record struct WindowsDiskPerformanceTarget(
    int DisplayIndex,
    string IdentityKey,
    bool SupportsNvmeHealth,
    bool SupportsAtaSmart)
{
    internal bool SupportsExactAtaNvmeIdentity => SupportsNvmeHealth || SupportsAtaSmart;
}

/// <summary>Results returned by a caller-supplied or default disk performance producer.</summary>
public sealed record WindowsDiskPerformanceCaptureResult(
    IReadOnlyDictionary<WindowsDiskPerformanceTarget, WindowsDiskPerformanceMetrics?> Readings)
{
    internal static WindowsDiskPerformanceCaptureResult Empty(IReadOnlyList<WindowsDiskPerformanceTarget> targets)
        => new(targets.Distinct().ToDictionary(static target => target, static _ => (WindowsDiskPerformanceMetrics?)null));
}

internal static class WindowsDiskPerformanceProjection
{
    internal static WindowsDiskPerformanceMetrics? Project(
        WindowsDiskPerformanceCounters start,
        WindowsDiskPerformanceCounters end,
        TimeSpan elapsed)
    {
        if (!string.Equals(start.StorageManagerName, "PARTMGR", StringComparison.Ordinal)
            || !string.Equals(end.StorageManagerName, "PARTMGR", StringComparison.Ordinal)
            || elapsed <= TimeSpan.Zero
            || start.BytesRead < 0 || start.BytesWritten < 0 || start.ReadTime < 0 || start.WriteTime < 0 || start.IdleTime < 0
            || end.BytesRead < start.BytesRead || end.BytesWritten < start.BytesWritten
            || end.ReadTime < start.ReadTime || end.WriteTime < start.WriteTime || end.IdleTime < start.IdleTime
            || end.ReadCount < start.ReadCount || end.WriteCount < start.WriteCount)
            return null;

        var elapsedSeconds = elapsed.TotalSeconds;
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return null;
        var idleDelta = end.IdleTime - start.IdleTime;
        if (idleDelta > elapsed.Ticks) return null;

        var readBytes = end.BytesRead - start.BytesRead;
        var writeBytes = end.BytesWritten - start.BytesWritten;
        var readCount = end.ReadCount - start.ReadCount;
        var writeCount = end.WriteCount - start.WriteCount;
        var readTime = end.ReadTime - start.ReadTime;
        var writeTime = end.WriteTime - start.WriteTime;
        var activePercent = 100d * (elapsed.Ticks - idleDelta) / elapsed.Ticks;
        var operations = (ulong)readCount + writeCount;
        double? latency = operations == 0
            ? null
            : ((double)readTime + writeTime) / operations / 10_000d;

        var metrics = new WindowsDiskPerformanceMetrics(
            (double)readBytes / elapsedSeconds,
            (double)writeBytes / elapsedSeconds,
            readCount / elapsedSeconds,
            writeCount / elapsedSeconds,
            activePercent,
            end.QueueDepth,
            latency);
        return double.IsFinite(metrics.ReadBytesPerSecond)
            && double.IsFinite(metrics.WriteBytesPerSecond)
            && double.IsFinite(metrics.ReadIops)
            && double.IsFinite(metrics.WriteIops)
            && double.IsFinite(metrics.ActivePercent)
            && metrics.ActivePercent is >= 0 and <= 100
            && double.IsFinite(metrics.QueueLength)
            && (metrics.LatencyMilliseconds is null || double.IsFinite(metrics.LatencyMilliseconds.Value))
                ? metrics
                : null;
    }
}
