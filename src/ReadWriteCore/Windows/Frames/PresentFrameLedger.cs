
namespace ResourceManager.ReadWrite.Windows.Frames;

/// <summary>
/// 帧时间戳账本：按进程实例（PID 加启动时间）和帧流保存最近的 Present 时间戳。
/// 不碰 ETW，由 the Host PresentFrameEtwSource 在事件线程上调用；调用方负责加锁。
/// </summary>
internal sealed class PresentFrameLedger(Func<int, long?> readProcessStartKey)
{
    public const int MaximumTimestampsPerStream = 16384;
    public const int MaximumStreams = 256;
    public static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);
    private const uint QueuePacketMmioFlip = 3;
    private const uint QueuePacketSoftware = 7;

    // 0 表示读不到启动时间：这个 PID 的帧先丢弃，直到进程启动或退出事件更新它。
    private readonly Dictionary<int, long> startKeys = [];
    private readonly Dictionary<StreamKey, Queue<long>> streams = [];
    // Keep a stopped process's last timestamps for cursor readers until normal retention expires.
    // Window statistics continue to see only live streams.
    private readonly Dictionary<StreamKey, Queue<long>> retiredStreams = [];
    private readonly Dictionary<StreamKey, Queue<DisplayFrameState>> displayFrames = [];
    private readonly Dictionary<ThreadKey, DisplayFrameState> currentPresentsByThread = [];
    private readonly Dictionary<uint, List<DisplayFrameState>> presentsBySubmitSequence = [];
    private readonly Dictionary<Win32kTokenKey, DisplayFrameState> presentsByWin32kToken = [];
    private readonly Dictionary<StreamKey, long> displayOverflowAtUtcTicks = [];

    public int StreamCount => streams.Count;

    public void ProcessStarted(int processId, long processStartKey)
    {
        ClearDisplayCorrelation(processId);
        RemoveProcess(processId);
        startKeys[processId] = processStartKey;
    }

    public void ProcessStopped(int processId)
    {
        ClearDisplayCorrelation(processId);
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

    public void Present(int processId, FramePresentSource source, ulong swapChain,
        long timestampUtcTicks, int threadId, long timestampQpc, long qpcFrequency)
    {
        Present(processId, source, swapChain, timestampUtcTicks);
        if (threadId <= 0 || swapChain == 0
            || !startKeys.TryGetValue(processId, out var processStartKey) || processStartKey == 0)
        {
            return;
        }

        var key = new StreamKey(processId, processStartKey, source, swapChain);
        if (!streams.ContainsKey(key))
        {
            return;
        }

        if (!displayFrames.TryGetValue(key, out var frames))
        {
            if (displayFrames.Count >= MaximumStreams)
            {
                return;
            }

            displayFrames[key] = frames = new Queue<DisplayFrameState>();
        }

        if (frames.Count == MaximumTimestampsPerStream)
        {
            var retired = frames.Dequeue();
            RemoveCorrelation(retired);
            displayOverflowAtUtcTicks[key] = timestampUtcTicks;
        }

        var frame = new DisplayFrameState(key, threadId, timestampUtcTicks,
            timestampQpc, qpcFrequency)
        {
            Ambiguous = timestampQpc <= 0 || qpcFrequency <= 0
        };
        frames.Enqueue(frame);
        var threadKey = new ThreadKey(processId, processStartKey, threadId);
        if (currentPresentsByThread.TryGetValue(threadKey, out var previous))
        {
            MarkAmbiguous(previous);
            RemoveCorrelation(previous);
        }

        currentPresentsByThread[threadKey] = frame;
    }

    public void LegacyFlip(int processId, int threadId, bool mmioFlip, long timestampQpc)
    {
        if (!TryGetCurrent(processId, threadId, out var frame)) return;
        if (frame.UnsupportedMode || frame.LegacyFlipSeen || timestampQpc <= frame.PresentStartQpc)
        {
            MarkAmbiguous(frame);
            return;
        }

        if (!mmioFlip)
        {
            frame.UnsupportedMode = true;
            return;
        }

        frame.LegacyFlipSeen = true;
        frame.LegacyFlipQpc = timestampQpc;
    }

    public void QueuePacketStart(int processId, int threadId, uint submitSequence,
        ulong context, uint packetType, bool isPresentPacket, long timestampQpc)
    {
        var relevantPacket = isPresentPacket
            || packetType is QueuePacketMmioFlip or QueuePacketSoftware;
        if (!relevantPacket || context == 0 || !TryGetCurrent(processId, threadId, out var frame)) return;
        if (frame.UnsupportedMode || !frame.LegacyFlipSeen || frame.QueuePacketSeen
            || submitSequence == 0 || timestampQpc <= frame.LegacyFlipQpc)
        {
            MarkAmbiguous(frame);
            return;
        }

        frame.QueuePacketSeen = true;
        frame.SubmitSequence = submitSequence;
        frame.QueuePacketQpc = timestampQpc;
        if (!presentsBySubmitSequence.TryGetValue(submitSequence, out var candidates))
        {
            presentsBySubmitSequence[submitSequence] = candidates = [];
        }

        if (candidates.Count > 0)
        {
            foreach (var candidate in candidates.ToArray()) MarkAmbiguous(candidate);
            MarkAmbiguous(frame);
            return;
        }

        candidates.Add(frame);
    }

    public void MmioFlip(uint submitSequence, long timestampUtcTicks, long timestampQpc, bool flipImmediate)
    {
        if (!TryGetUniqueSubmitSequence(submitSequence, out var frame)) return;
        if (!frame.QueuePacketSeen || frame.UnsupportedMode || frame.Ambiguous
            || timestampQpc <= frame.QueuePacketQpc)
        {
            MarkAmbiguous(frame);
            return;
        }

        frame.MmioFlipSeen = true;
        frame.MmioFlipQpc = timestampQpc;
        if (flipImmediate)
        {
            SetOutcome(frame, FrameTimingDisplayOutcome.Displayed, timestampUtcTicks, timestampQpc);
        }
    }

    public void VSyncDpc(uint submitSequence, long timestampUtcTicks, long timestampQpc)
    {
        if (!TryGetUniqueSubmitSequence(submitSequence, out var frame)) return;
        if (!frame.LegacyFlipSeen || !frame.QueuePacketSeen || !frame.MmioFlipSeen
            || frame.UnsupportedMode || frame.Ambiguous
            || timestampQpc <= frame.MmioFlipQpc || timestampQpc <= frame.PresentStartQpc
            || frame.QpcFrequency <= 0)
        {
            MarkAmbiguous(frame);
            return;
        }

        SetOutcome(frame, FrameTimingDisplayOutcome.Displayed, timestampUtcTicks, timestampQpc);
    }

    public void PresentStopped(int processId, int threadId, long timestampUtcTicks,
        long timestampQpc, int result)
    {
        if (!TryGetCurrent(processId, threadId, out var frame)) return;
        if (result < 0)
        {
            SetOutcome(frame, FrameTimingDisplayOutcome.Failed, timestampUtcTicks, timestampQpc);
        }
        else
        {
            RemoveCurrent(frame);
        }
    }

    public void TrackWin32kToken(int processId, int threadId,
        ulong compositionSurfaceLuid, ulong presentCount, ulong bindId)
    {
        if (!TryGetCurrent(processId, threadId, out var frame)) return;
        var key = new Win32kTokenKey(compositionSurfaceLuid, presentCount, bindId);
        if (frame.Win32kToken is { } currentToken)
        {
            if (currentToken != key) MarkAmbiguous(frame);
            return;
        }

        frame.UnsupportedMode = true;
        frame.CompositionSurfaceLuid = compositionSurfaceLuid;
        if (presentsByWin32kToken.TryGetValue(key, out var previous) && !ReferenceEquals(previous, frame))
        {
            MarkAmbiguous(previous);
            MarkAmbiguous(frame);
            presentsByWin32kToken.Remove(key);
            return;
        }

        frame.Win32kToken = key;
        presentsByWin32kToken[key] = frame;
    }

    public void Win32kTokenStateChanged(ulong compositionSurfaceLuid,
        ulong presentCount, ulong bindId, uint state, long timestampUtcTicks, long timestampQpc)
    {
        var key = new Win32kTokenKey(compositionSurfaceLuid, presentCount, bindId);
        if (!presentsByWin32kToken.TryGetValue(key, out var frame)) return;
        if (state == 3)
        {
            frame.Win32kSeenInFrame = true;
        }
        else if (state == 6 && !frame.Win32kSeenInFrame
            && frame.Outcome == FrameTimingDisplayOutcome.Unknown)
        {
            SetOutcome(frame, FrameTimingDisplayOutcome.Discarded, timestampUtcTicks, timestampQpc);
        }
        else if (state == 6 && frame.Outcome != FrameTimingDisplayOutcome.Displayed)
        {
            MarkAmbiguous(frame);
        }
        else if (state == 5 && frame.Outcome == FrameTimingDisplayOutcome.Unknown)
        {
            frame.Outcome = FrameTimingDisplayOutcome.Retired;
            frame.OutcomeAtUtcTicks = timestampUtcTicks;
            frame.OutcomeQpc = timestampQpc;
        }
    }

    public void UnsupportedDisplayPath(int processId, int threadId, uint? vidPnSourceId = null)
    {
        if (!TryGetCurrent(processId, threadId, out var frame)) return;
        frame.UnsupportedMode = true;
        frame.VidPnSourceId = vidPnSourceId;
    }

    public IReadOnlyList<FrameTimingDisplayStream> ReadDisplayStreams(long nowUtcTicks, TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        Prune(nowUtcTicks - Retention.Ticks);
        var windowStart = nowUtcTicks - window.Ticks;
        var result = new List<FrameTimingDisplayStream>();
        foreach (var (key, queue) in displayFrames)
        {
            var frames = queue.ToArray();
            var startsInWindow = frames.Where(frame =>
                frame.PresentStartedAtUtcTicks >= windowStart && frame.PresentStartedAtUtcTicks <= nowUtcTicks).ToArray();
            var outcomesInWindow = frames.Where(frame =>
                frame.OutcomeAtUtcTicks is { } ended && ended >= windowStart && ended <= nowUtcTicks).ToArray();
            if (startsInWindow.Length == 0 && outcomesInWindow.Length == 0)
            {
                continue;
            }

            var allOutcomesKnown = frames.All(frame => !frame.Ambiguous && frame.Outcome is
                FrameTimingDisplayOutcome.Displayed or FrameTimingDisplayOutcome.Discarded);
            var commonFrequency = frames.Select(static frame => frame.QpcFrequency).Distinct().Take(2).ToArray();
            var complete = window <= Retention
                && (!displayOverflowAtUtcTicks.TryGetValue(key, out var overflowAt)
                    || overflowAt < windowStart || overflowAt > nowUtcTicks)
                && allOutcomesKnown
                && commonFrequency.Length == 1
                && commonFrequency[0] > 0;

            FrameIntervalStatistics? statistics = null;
            if (complete)
            {
                var displayed = frames.Where(frame => frame.Outcome == FrameTimingDisplayOutcome.Displayed
                        && frame.OutcomeAtUtcTicks is { } ended && ended <= nowUtcTicks)
                    .OrderBy(static frame => frame.OutcomeQpc)
                    .ToArray();
                var inWindow = displayed.Where(frame => frame.OutcomeAtUtcTicks is { } ended && ended >= windowStart).ToArray();
                if (inWindow.Length > 0)
                {
                    var previous = displayed.LastOrDefault(frame =>
                        frame.OutcomeAtUtcTicks is { } ended && ended < windowStart);
                    DisplayFrameState[] samples = previous is null
                        ? inWindow
                        : [previous, .. inWindow];
                    var intervals = new double[Math.Max(0, samples.Length - 1)];
                    var intervalCount = 0;
                    for (var index = 1; index < samples.Length; index++)
                    {
                        if (samples[index].OutcomeQpc is { } currentQpc
                            && samples[index - 1].OutcomeQpc is { } previousQpc)
                        {
                            var qpcDelta = currentQpc - previousQpc;
                            if (qpcDelta > 0)
                            {
                                intervals[intervalCount++] = qpcDelta * 1000d / commonFrequency[0];
                            }
                        }
                    }

                    statistics = FrameIntervalStatistics.Compute(intervals.AsSpan(0, intervalCount));
                }
            }

            var droppedCount = complete
                ? checked((ulong)outcomesInWindow.Count(static frame =>
                    frame.Outcome == FrameTimingDisplayOutcome.Discarded))
                : (ulong?)null;
            var returnedFrames = frames.Where(frame =>
                    (frame.PresentStartedAtUtcTicks >= windowStart && frame.PresentStartedAtUtcTicks <= nowUtcTicks)
                    || (frame.OutcomeAtUtcTicks is { } ended && ended >= windowStart && ended <= nowUtcTicks))
                .Select(ToDisplayFrame)
                .ToArray();
            result.Add(new FrameTimingDisplayStream(key.ProcessId, key.ProcessStartKey, key.Source,
                key.SwapChain, complete, statistics, droppedCount, returnedFrames));
        }

        return result;
    }

    private static FrameTimingDisplayFrame ToDisplayFrame(DisplayFrameState frame)
        => new(new DateTimeOffset(frame.PresentStartedAtUtcTicks, TimeSpan.Zero), frame.Outcome,
            frame.OutcomeAtUtcTicks is { } ended ? new DateTimeOffset(ended, TimeSpan.Zero) : null,
            frame.PresentStartQpc,
            frame.Outcome == FrameTimingDisplayOutcome.Displayed ? frame.OutcomeQpc : null,
            frame.OutcomeQpc,
            frame.QpcFrequency,
            null,
            null,
            frame.VidPnSourceId,
            frame.CompositionSurfaceLuid);

    private bool TryGetCurrent(int processId, int threadId, out DisplayFrameState frame)
    {
        frame = null!;
        if (processId <= 0 || threadId <= 0
            || !startKeys.TryGetValue(processId, out var processStartKey)
            || !currentPresentsByThread.TryGetValue(new ThreadKey(processId, processStartKey, threadId), out var current))
        {
            return false;
        }

        frame = current;
        return true;
    }

    private bool TryGetUniqueSubmitSequence(uint submitSequence, out DisplayFrameState frame)
    {
        frame = null!;
        if (!presentsBySubmitSequence.TryGetValue(submitSequence, out var candidates)) return false;
        if (candidates.Count != 1)
        {
            foreach (var candidate in candidates.ToArray()) MarkAmbiguous(candidate);
            return false;
        }

        frame = candidates[0];
        return !frame.Ambiguous;
    }

    private void SetOutcome(DisplayFrameState frame, FrameTimingDisplayOutcome outcome,
        long timestampUtcTicks, long timestampQpc)
    {
        if (frame.Ambiguous || frame.QpcFrequency <= 0 || timestampQpc <= frame.PresentStartQpc)
        {
            MarkAmbiguous(frame);
            return;
        }

        if (frame.Outcome is FrameTimingDisplayOutcome.Displayed or FrameTimingDisplayOutcome.Discarded
            or FrameTimingDisplayOutcome.Failed)
        {
            if (frame.Outcome != outcome) MarkAmbiguous(frame);
            return;
        }

        frame.Outcome = outcome;
        frame.OutcomeAtUtcTicks = timestampUtcTicks;
        frame.OutcomeQpc = timestampQpc;
        RemoveCorrelation(frame);
    }

    private void MarkAmbiguous(DisplayFrameState frame)
    {
        frame.Ambiguous = true;
        frame.Outcome = FrameTimingDisplayOutcome.Unknown;
        frame.OutcomeAtUtcTicks = null;
        frame.OutcomeQpc = null;
        RemoveCorrelation(frame);
    }

    private void RemoveCurrent(DisplayFrameState frame)
    {
        var key = new ThreadKey(frame.Stream.ProcessId, frame.Stream.ProcessStartKey, frame.ThreadId);
        if (currentPresentsByThread.TryGetValue(key, out var current) && ReferenceEquals(current, frame))
        {
            currentPresentsByThread.Remove(key);
        }
    }

    private void RemoveCorrelation(DisplayFrameState frame)
    {
        RemoveCurrent(frame);
        if (frame.SubmitSequence is { } sequence
            && presentsBySubmitSequence.TryGetValue(sequence, out var candidates))
        {
            candidates.Remove(frame);
            if (candidates.Count == 0) presentsBySubmitSequence.Remove(sequence);
        }

        if (frame.Win32kToken is { } token
            && presentsByWin32kToken.TryGetValue(token, out var tracked)
            && ReferenceEquals(tracked, frame))
        {
            presentsByWin32kToken.Remove(token);
        }
    }

    private void ClearDisplayCorrelation(int processId)
    {
        foreach (var key in currentPresentsByThread.Keys.Where(key => key.ProcessId == processId).ToArray())
        {
            currentPresentsByThread.Remove(key);
        }
        foreach (var (sequence, candidates) in presentsBySubmitSequence.ToArray())
        {
            candidates.RemoveAll(frame => frame.Stream.ProcessId == processId);
            if (candidates.Count == 0) presentsBySubmitSequence.Remove(sequence);
        }
        foreach (var (token, frame) in presentsByWin32kToken.ToArray())
        {
            if (frame.Stream.ProcessId == processId) presentsByWin32kToken.Remove(token);
        }
    }

    private void PruneDisplay(long oldestKeptTicks)
    {
        foreach (var (key, frames) in displayFrames.ToArray())
        {
            while (frames.Count > 0 && frames.Peek().PresentStartedAtUtcTicks < oldestKeptTicks)
            {
                var frame = frames.Dequeue();
                RemoveCorrelation(frame);
            }

            if (frames.Count == 0) displayFrames.Remove(key);
        }

        foreach (var (key, overflowAt) in displayOverflowAtUtcTicks.ToArray())
        {
            if (overflowAt < oldestKeptTicks)
            {
                displayOverflowAtUtcTicks.Remove(key);
            }
        }
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
                .Select(pair => Measure(pair.Key, pair.Value, windowStart, nowUtcTicks))
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

    public IReadOnlyList<FrameTimingPresent> ReadLastPresents(long nowUtcTicks, TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        Prune(nowUtcTicks - Retention.Ticks);
        var windowStart = nowUtcTicks - window.Ticks;
        return streams
            .Select(pair => (pair.Key, Last: pair.Value
                .Where(timestamp => timestamp >= windowStart && timestamp <= nowUtcTicks)
                .DefaultIfEmpty(long.MinValue)
                .Max()))
            .Where(item => item.Last != long.MinValue)
            .OrderBy(item => item.Key.ProcessId)
            .ThenBy(item => item.Key.ProcessStartKey)
            .ThenBy(item => item.Key.Source)
            .ThenBy(item => item.Key.SwapChain)
            .Select(item => new FrameTimingPresent(item.Key.ProcessId, item.Key.ProcessStartKey,
                item.Key.Source, item.Key.SwapChain, new DateTimeOffset(item.Last, TimeSpan.Zero)))
            .ToArray();
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

    private static FrameTimingStream? Measure(
        StreamKey key, Queue<long> timestamps, long windowStart, long windowEnd)
    {
        // 实时会话按 CPU 缓冲投递，换核的渲染线程可能乱序到达，先排序再取间隔。
        var inWindow = timestamps.Where(timestamp => timestamp >= windowStart && timestamp <= windowEnd).ToArray();
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
        PruneDisplay(oldestKeptTicks);
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
    private readonly record struct ThreadKey(int ProcessId, long ProcessStartKey, int ThreadId);
    private readonly record struct Win32kTokenKey(ulong CompositionSurfaceLuid, ulong PresentCount, ulong BindId);

    private sealed class DisplayFrameState(
        StreamKey stream,
        int threadId,
        long presentStartedAtUtcTicks,
        long presentStartQpc,
        long qpcFrequency)
    {
        public StreamKey Stream { get; } = stream;
        public int ThreadId { get; } = threadId;
        public long PresentStartedAtUtcTicks { get; } = presentStartedAtUtcTicks;
        public long PresentStartQpc { get; } = presentStartQpc;
        public long QpcFrequency { get; } = qpcFrequency;
        public FrameTimingDisplayOutcome Outcome { get; set; }
        public long? OutcomeAtUtcTicks { get; set; }
        public long? OutcomeQpc { get; set; }
        public uint? VidPnSourceId { get; set; }
        public ulong? CompositionSurfaceLuid { get; set; }
        public uint? SubmitSequence { get; set; }
        public long QueuePacketQpc { get; set; }
        public Win32kTokenKey? Win32kToken { get; set; }
        public long MmioFlipQpc { get; set; }
        public long LegacyFlipQpc { get; set; }
        public bool LegacyFlipSeen { get; set; }
        public bool QueuePacketSeen { get; set; }
        public bool MmioFlipSeen { get; set; }
        public bool Win32kSeenInFrame { get; set; }
        public bool UnsupportedMode { get; set; }
        public bool Ambiguous { get; set; }
    }
}
