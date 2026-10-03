using ResourceManager.App.Domain.FrameTiming;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>
/// 帧时间戳账本：按进程实例（PID 加启动时间）和帧流保存最近的 Present 时间戳。
/// 不碰 ETW，由 <see cref="PresentFrameEtwSource"/> 在事件线程上调用；调用方负责加锁。
/// </summary>
internal sealed class PresentFrameLedger(Func<int, long?> readProcessStartKey)
{
    public const int MaximumTimestampsPerStream = 16384;
    public const int MaximumStreams = 256;
    public static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);

    // 0 表示读不到启动时间：这个 PID 的帧先丢弃，直到进程启动或退出事件更新它。
    private readonly Dictionary<int, long> startKeys = [];
    private readonly Dictionary<StreamKey, Queue<long>> streams = [];
    // Keep a stopped process's last timestamps for cursor readers until normal retention expires.
    // Window statistics continue to see only live streams.
    private readonly Dictionary<StreamKey, Queue<long>> retiredStreams = [];

    public int StreamCount => streams.Count;

    public void ProcessStarted(int processId, long processStartKey)
    {
        RemoveProcess(processId);
        startKeys[processId] = processStartKey;
    }

    public void ProcessStopped(int processId)
    {
        RemoveProcess(processId);
        startKeys.Remove(processId);
    }

    public void Present(int processId, FramePresentSource source, ulong swapChain, long timestampUtcTicks)
    {
        if (processId <= 0)
        {
            return;
        }

        if (!startKeys.TryGetValue(processId, out var startKey))
        {
            startKey = readProcessStartKey(processId) is > 0 and var read ? read : 0;
            startKeys[processId] = startKey;
        }

        if (startKey == 0)
        {
            return;
        }

        var key = new StreamKey(processId, startKey, source, swapChain);
        if (!streams.TryGetValue(key, out var timestamps))
        {
            if (streams.Count >= MaximumStreams)
            {
                return;
            }

            streams[key] = timestamps = new Queue<long>();
        }

        if (timestamps.Count == MaximumTimestampsPerStream)
        {
            timestamps.Dequeue();
        }

        timestamps.Enqueue(timestampUtcTicks);
    }

    public IReadOnlyList<FrameTimingProcess> Read(long nowUtcTicks, TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        Prune(nowUtcTicks - Retention.Ticks);
        var windowStart = nowUtcTicks - window.Ticks;
        var processes = new List<FrameTimingProcess>();
        foreach (var process in streams.GroupBy(pair => (pair.Key.ProcessId, pair.Key.ProcessStartKey)))
        {
            var measured = process
                .Select(pair => Measure(pair.Key, pair.Value, windowStart))
                .OfType<FrameTimingStream>()
                .ToArray();
            // 同一帧在运行时和图形内核各记一次；有运行时帧流时只报运行时。
            var reported = measured.Any(stream => stream.Source != FramePresentSource.GraphicsKernel)
                ? measured.Where(stream => stream.Source != FramePresentSource.GraphicsKernel).ToArray()
                : measured;
            if (reported.Length > 0)
            {
                processes.Add(new FrameTimingProcess(process.Key.ProcessId, process.Key.ProcessStartKey, reported));
            }
        }

        return processes;
    }

    public IReadOnlyList<FrameIntervalSample> ReadIntervals(long afterUtcTicks, long throughUtcTicks)
    {
        if (throughUtcTicks <= afterUtcTicks)
        {
            return [];
        }

        Prune(throughUtcTicks - Retention.Ticks);
        var result = new List<FrameIntervalSample>();
        foreach (var process in streams.Concat(retiredStreams)
            .GroupBy(pair => (pair.Key.ProcessId, pair.Key.ProcessStartKey)))
        {
            // Match Read's runtime-over-kernel rule within the requested interval.
            var candidates = process.Select(pair => (pair.Key, Timestamps: pair.Value
                .Where(timestamp => timestamp <= throughUtcTicks && timestamp >= afterUtcTicks - Retention.Ticks)
                .Order().ToArray())).ToArray();
            var hasRuntime = candidates.Any(item => item.Key.Source != FramePresentSource.GraphicsKernel
                && item.Timestamps.Length > 1 && item.Timestamps[^1] > afterUtcTicks);
            foreach (var (key, timestamps) in candidates)
            {
                if (hasRuntime && key.Source == FramePresentSource.GraphicsKernel)
                {
                    continue;
                }

                for (var index = 1; index < timestamps.Length; index++)
                {
                    var endedAt = timestamps[index];
                    var durationTicks = endedAt - timestamps[index - 1];
                    if (endedAt <= afterUtcTicks || durationTicks <= 0)
                    {
                        continue;
                    }

                    result.Add(new FrameIntervalSample(key.ProcessId, key.ProcessStartKey,
                        key.Source, key.SwapChain,
                        new DateTimeOffset(endedAt, TimeSpan.Zero),
                        TimeSpan.FromTicks(durationTicks).TotalMilliseconds));
                }
            }
        }

        result.Sort(static (left, right) => left.EndedAt.CompareTo(right.EndedAt));
        return result;
    }

    private static FrameTimingStream? Measure(StreamKey key, Queue<long> timestamps, long windowStart)
    {
        // 实时会话按 CPU 缓冲投递，换核的渲染线程可能乱序到达，先排序再取间隔。
        var inWindow = timestamps.Where(timestamp => timestamp >= windowStart).ToArray();
        if (inWindow.Length < 2)
        {
            return null;
        }

        Array.Sort(inWindow);
        var intervals = new double[inWindow.Length - 1];
        var count = 0;
        for (var index = 1; index < inWindow.Length; index++)
        {
            var ticks = inWindow[index] - inWindow[index - 1];
            if (ticks > 0)
            {
                intervals[count++] = TimeSpan.FromTicks(ticks).TotalMilliseconds;
            }
        }

        var statistics = FrameIntervalStatistics.Compute(intervals.AsSpan(0, count));
        return statistics is null
            ? null
            : new FrameTimingStream(key.Source, key.SwapChain, new DateTimeOffset(inWindow[^1], TimeSpan.Zero), statistics)
            {
                PresentCount = inWindow.Length
            };
    }

    private void Prune(long oldestKeptTicks)
    {
        foreach (var (key, timestamps) in streams.ToArray())
        {
            while (timestamps.Count > 0 && timestamps.Peek() < oldestKeptTicks)
            {
                timestamps.Dequeue();
            }

            if (timestamps.Count == 0)
            {
                streams.Remove(key);
            }
        }
        foreach (var (key, timestamps) in retiredStreams.ToArray())
        {
            while (timestamps.Count > 0 && timestamps.Peek() < oldestKeptTicks)
            {
                timestamps.Dequeue();
            }
            if (timestamps.Count == 0)
            {
                retiredStreams.Remove(key);
            }
        }
    }

    private void RemoveProcess(int processId)
    {
        foreach (var key in streams.Keys.Where(key => key.ProcessId == processId).ToArray())
        {
            if (retiredStreams.Count < MaximumStreams)
            {
                retiredStreams[key] = streams[key];
            }
            streams.Remove(key);
        }
    }

    private readonly record struct StreamKey(int ProcessId, long ProcessStartKey, FramePresentSource Source, ulong SwapChain);
}
