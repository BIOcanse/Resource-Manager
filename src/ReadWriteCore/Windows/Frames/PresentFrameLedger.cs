
namespace ResourceManager.ReadWrite.Windows.Frames;

/// <summary>
/// 帧账本：按进程实例（PID 加启动时间）和帧流保存最近的 Present（起止时刻、显示结果、输出尺寸）。
/// 不碰 ETW，由 the Host PresentFrameEtwSource 在事件线程上调用；调用方负责加锁。
/// 上限和保留时长来自 frame_pipeline 自由点（<see cref="PresentFrameLedgerLimits"/>）。
/// </summary>
internal sealed class PresentFrameLedger(Func<int, long?> readProcessStartKey, PresentFrameLedgerLimits limits)
{
    private const uint QueuePacketMmioFlip = 3;
    private const uint QueuePacketSoftware = 7;

    // 0 表示读不到启动时间：这个 PID 的帧先丢弃，直到进程启动或退出事件更新它。
    private readonly Dictionary<int, long> startKeys = [];
    private readonly Dictionary<StreamKey, PresentTimeline> streams = [];
    // Keep a stopped process's last Presents for cursor readers until normal retention expires.
    // Window statistics continue to see only live streams.
    private readonly Dictionary<StreamKey, PresentTimeline> retiredStreams = [];
    // A runtime Present waiting for its stop on the same thread.
    private readonly Dictionary<ThreadKey, (StreamKey Stream, long Sequence)> pendingStops = [];
    private readonly Dictionary<StreamKey, Queue<DisplayFrameState>> displayFrames = [];
    private readonly Dictionary<ThreadKey, DisplayFrameState> currentPresentsByThread = [];
    private readonly Dictionary<uint, List<DisplayFrameState>> presentsBySubmitSequence = [];
    private readonly Dictionary<Win32kTokenKey, DisplayFrameState> presentsByWin32kToken = [];
    private readonly Dictionary<StreamKey, long> displayOverflowAtUtcTicks = [];
    private readonly Dictionary<(int ProcessId, long ProcessStartKey, ulong SwapChain), FrameOutputResolution> swapChainSizes = [];
    private readonly Dictionary<StreamKey, FrameOutputResolution> compositionSizes = [];
    private long streamCapacityThroughUtcTicks;
    private long presentRateCapacityThroughUtcTicks;

    public PresentFrameLedgerLimits Limits { get; } = limits.Validate();
    public int StreamCount => streams.Count;

    /// <summary>Set by the owner while the GPU frame tier runs; Present stops close GPU frames through it.</summary>
    public IGpuFrameBoundary? GpuFrames { get; set; }

    // Keep the bounded output history, release the indexes used only to correlate
    // incoming events. A stopped producer can still return its current values.
    public void ReleaseCorrelationState()
    {
        startKeys.Clear();
        pendingStops.Clear();
        currentPresentsByThread.Clear();
        presentsBySubmitSequence.Clear();
        presentsByWin32kToken.Clear();
    }

    // Lost lifecycle events invalidate live identity/correlation state. Exact recorded
    // history remains readable by cursor consumers until normal retention expires.
    public void EventsLost()
    {
        foreach (var processId in startKeys.Keys.ToArray()) RemoveProcess(processId);
        startKeys.Clear();
        foreach (var frame in displayFrames.Values.SelectMany(static frames => frames))
        {
            if (frame.Outcome == FrameTimingDisplayOutcome.Unknown)
            {
                frame.Ambiguous = true;
                frame.LostEvents = true;
            }
        }
        pendingStops.Clear();
        currentPresentsByThread.Clear();
        presentsBySubmitSequence.Clear();
        presentsByWin32kToken.Clear();
    }

    public bool IsComplete(long afterUtcTicks) => IncompleteReasons(afterUtcTicks).Count == 0;

    /// <summary>Ledger-owned reasons a range starting at <paramref name="afterUtcTicks"/> is incomplete.</summary>
    public IReadOnlyList<string> IncompleteReasons(long afterUtcTicks)
    {
        var reasons = new List<string>(2);
        if (streamCapacityThroughUtcTicks >= afterUtcTicks) reasons.Add(FrameTimingReasons.StreamCapacity);
        if (presentRateCapacityThroughUtcTicks >= afterUtcTicks) reasons.Add(FrameTimingReasons.PresentRateCapacity);
        return reasons;
    }

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
        => _ = Append(processId, source, swapChain, timestampUtcTicks);

    private (StreamKey Key, long Sequence)? Append(int processId, FramePresentSource source, ulong swapChain, long timestampUtcTicks)
    {
        if (processId <= 0)
        {
            return null;
        }

        if (!startKeys.TryGetValue(processId, out var startKey))
        {
            startKey = readProcessStartKey(processId) is > 0 and var read ? read : 0;
            startKeys[processId] = startKey;
        }

        if (startKey == 0)
        {
            return null;
        }

        var key = new StreamKey(processId, startKey, source, swapChain);
        if (!streams.TryGetValue(key, out var timeline))
        {
            if (streams.Count >= Limits.MaximumStreams)
            {
                streamCapacityThroughUtcTicks = Math.Max(streamCapacityThroughUtcTicks, timestampUtcTicks);
                return null;
            }

            streams[key] = timeline = new PresentTimeline(Limits.PresentsPerStream);
        }

        var sequence = timeline.Append(timestampUtcTicks, out var overwritten);
        if (overwritten is { } lost)
        {
            presentRateCapacityThroughUtcTicks = Math.Max(presentRateCapacityThroughUtcTicks, lost);
            // The new oldest Present also loses its predecessor interval.
            presentRateCapacityThroughUtcTicks = Math.Max(presentRateCapacityThroughUtcTicks, timeline.FirstStart);
        }

        return (key, sequence);
    }

    public void Present(int processId, FramePresentSource source, ulong swapChain,
        long timestampUtcTicks, int threadId, long timestampQpc, long qpcFrequency)
    {
        var appended = Append(processId, source, swapChain, timestampUtcTicks);
        if (threadId <= 0 || swapChain == 0 || appended is not { } entry)
        {
            return;
        }

        var (key, sequence) = entry;

        var threadKey = new ThreadKey(processId, key.ProcessStartKey, threadId);
        pendingStops[threadKey] = (key, sequence);
        if (!displayFrames.TryGetValue(key, out var frames))
        {
            if (displayFrames.Count >= Limits.MaximumStreams)
            {
                return;
            }

            displayFrames[key] = frames = new Queue<DisplayFrameState>();
        }

        if (frames.Count == Limits.PresentsPerStream)
        {
            var retired = frames.Dequeue();
            RemoveCorrelation(retired);
            displayOverflowAtUtcTicks[key] = timestampUtcTicks;
        }

        var frame = new DisplayFrameState(key, threadId, sequence, timestampUtcTicks,
            timestampQpc, qpcFrequency)
        {
            Ambiguous = timestampQpc <= 0 || qpcFrequency <= 0
        };
        frames.Enqueue(frame);
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
        frame.PathSeen = true;
        if (frame.UnsupportedReason is not null || frame.LegacyFlipSeen || timestampQpc <= frame.PresentStartQpc)
        {
            MarkAmbiguous(frame);
            return;
        }

        if (!mmioFlip)
        {
            frame.UnsupportedReason = FrameTimingReasons.LegacyFlipWithoutMmioNotParsed;
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
        if (frame.UnsupportedReason is not null || !frame.LegacyFlipSeen || frame.QueuePacketSeen
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
        if (!frame.QueuePacketSeen || frame.UnsupportedReason is not null || frame.Ambiguous
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
            || frame.UnsupportedReason is not null || frame.Ambiguous
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
        if (processId > 0 && threadId > 0 && startKeys.TryGetValue(processId, out var processStartKey)
            && pendingStops.Remove(new ThreadKey(processId, processStartKey, threadId), out var pending)
            && streams.TryGetValue(pending.Stream, out var timeline)
            && timeline.TrySetStop(pending.Sequence, timestampUtcTicks))
        {
            // CPU completion is independent of GPU completion (the Present queue packet closes GPU work).
        }

        if (!TryGetCurrent(processId, threadId, out var frame)) return;
        if (result < 0)
        {
            SetOutcome(frame, FrameTimingDisplayOutcome.Failed, timestampUtcTicks, timestampQpc);
        }
        else
        {
            // Every parsed path logs its first event inside the Present call; none means blit, DWM or independent flip.
            if (!frame.PathSeen) frame.NoParsedPath = true;
            RemoveCurrent(frame);
        }
    }

    /// <summary>Called by the GPU frame tier when every packet of a closed frame completed.</summary>
    internal void SetGpuFrame(StreamKey stream, long sequence, PresentGpuRecord record)
    {
        if (streams.TryGetValue(stream, out var timeline) || retiredStreams.TryGetValue(stream, out timeline))
            timeline.TrySetGpu(sequence, record);
    }

    internal GpuFrameOwner? CurrentGpuOwner(int processId, int threadId)
        => startKeys.TryGetValue(processId, out var birth)
            && pendingStops.TryGetValue(new ThreadKey(processId, birth, threadId), out var pending)
                ? new GpuFrameOwner(this, pending.Stream, pending.Sequence) : null;

    public void TrackWin32kToken(int processId, int threadId,
        ulong compositionSurfaceLuid, ulong presentCount, ulong bindId,
        long timestampUtcTicks = 0, uint destWidth = 0, uint destHeight = 0)
    {
        if (!TryGetCurrent(processId, threadId, out var frame)) return;
        frame.PathSeen = true;
        if (destWidth > 0 && destHeight > 0 && destWidth <= int.MaxValue && destHeight <= int.MaxValue
            && (compositionSizes.ContainsKey(frame.Stream) || compositionSizes.Count < Limits.MaximumStreams))
        {
            compositionSizes[frame.Stream] = new FrameOutputResolution(FrameOutputResolutionSource.Win32kCompositionToken,
                (int)destWidth, (int)destHeight, new DateTimeOffset(timestampUtcTicks, TimeSpan.Zero));
        }

        var key = new Win32kTokenKey(compositionSurfaceLuid, presentCount, bindId);
        if (frame.Win32kToken is { } currentToken)
        {
            if (currentToken != key) MarkAmbiguous(frame);
            return;
        }

        frame.UnsupportedReason = FrameTimingReasons.ComposedScreenTimeNotParsed;
        frame.ComposedFlip = true;
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

    /// <summary>A display path whose screen time the ledger does not parse; the frame keeps the concrete reason.</summary>
    public void UnsupportedDisplayPath(int processId, int threadId, string reason, uint? vidPnSourceId = null)
    {
        if (!TryGetCurrent(processId, threadId, out var frame)) return;
        frame.PathSeen = true;
        frame.UnsupportedReason = reason;
        if (reason == FrameTimingReasons.MultiplaneOverlayNotParsed) frame.MultiplaneOverlay = true;
        frame.VidPnSourceId = vidPnSourceId;
    }

    /// <summary>DXGI SwapChain_Start / DC_Start (10/12) or a successful ResizeBuffers_Stop (46).</summary>
    public void SwapChainSize(int processId, ulong swapChain, uint width, uint height, long timestampUtcTicks)
    {
        if (processId <= 0 || swapChain == 0 || width == 0 || height == 0
            || width > int.MaxValue || height > int.MaxValue) return;
        if (!startKeys.TryGetValue(processId, out var startKey))
        {
            startKey = readProcessStartKey(processId) is > 0 and var read ? read : 0;
            startKeys[processId] = startKey;
        }

        if (startKey == 0) return;
        var key = (processId, startKey, swapChain);
        if (!swapChainSizes.ContainsKey(key) && swapChainSizes.Count >= Limits.MaximumStreams) return;
        swapChainSizes[key] = new FrameOutputResolution(FrameOutputResolutionSource.DxgiSwapChain,
            (int)width, (int)height, new DateTimeOffset(timestampUtcTicks, TimeSpan.Zero));
    }

    public IReadOnlyList<FrameTimingDisplayStream> ReadDisplayStreams(long nowUtcTicks, TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        Prune(nowUtcTicks - Limits.Retention.Ticks);
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

            var reasons = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var frame in frames)
            {
                if (Reason(frame) is { } reason) reasons.Add(reason);
            }

            var commonFrequency = frames.Select(static frame => frame.QpcFrequency).Distinct().Take(2).ToArray();
            if (commonFrequency.Length != 1 || commonFrequency[0] <= 0) reasons.Add(FrameTimingReasons.QpcFrequencyMismatch);
            if (displayOverflowAtUtcTicks.TryGetValue(key, out var overflowAt)
                && overflowAt >= windowStart && overflowAt <= nowUtcTicks) reasons.Add(FrameTimingReasons.DisplayCapacity);
            var complete = window <= Limits.Retention && reasons.Count == 0;

            var cpuStarts = CpuStarts(key, windowStart - Limits.Retention.Ticks, nowUtcTicks);
            var measured = new Dictionary<DisplayFrameState, DisplayedValues>();
            FrameIntervalStatistics? statistics = null;
            FrameDurationStatistics? displayedTime = null;
            FrameDurationStatistics? displayLatency = null;
            if (complete)
            {
                var displayed = frames.Where(frame => frame.Outcome == FrameTimingDisplayOutcome.Displayed
                        && frame.OutcomeAtUtcTicks is { } ended && ended <= nowUtcTicks && frame.OutcomeQpc is not null)
                    .OrderBy(static frame => frame.OutcomeQpc)
                    .ToArray();
                var frequency = commonFrequency[0];
                var between = new List<double>();
                var shown = new List<double>();
                var latencies = new List<double>();
                for (var index = 0; index < displayed.Length; index++)
                {
                    var frame = displayed[index];
                    if (frame.OutcomeAtUtcTicks < windowStart) continue;
                    var screen = frame.OutcomeQpc!.Value;
                    double? betweenMs = index > 0 && screen > displayed[index - 1].OutcomeQpc
                        ? (screen - displayed[index - 1].OutcomeQpc!.Value) * 1000d / frequency : null;
                    double? shownMs = index + 1 < displayed.Length && displayed[index + 1].OutcomeQpc > screen
                        ? (displayed[index + 1].OutcomeQpc!.Value - screen) * 1000d / frequency : null;
                    double? untilMs = screen > frame.PresentStartQpc ? (screen - frame.PresentStartQpc) * 1000d / frequency : null;
                    double? latencyMs = cpuStarts.TryGetValue(frame.Sequence, out var cpuStart)
                        ? TimeSpan.FromTicks(Math.Max(0, frame.OutcomeAtUtcTicks!.Value - cpuStart)).TotalMilliseconds : null;
                    measured[frame] = new DisplayedValues(betweenMs, shownMs, untilMs, latencyMs);
                    if (betweenMs is { } b) between.Add(b);
                    if (shownMs is { } s) shown.Add(s);
                    if (latencyMs is { } l) latencies.Add(l);
                }

                statistics = FrameIntervalStatistics.Compute(between.ToArray());
                displayedTime = FrameDurationStatistics.Compute(shown.ToArray());
                displayLatency = FrameDurationStatistics.Compute(latencies.ToArray());
            }

            var droppedCount = complete
                ? checked((ulong)outcomesInWindow.Count(static frame =>
                    frame.Outcome == FrameTimingDisplayOutcome.Discarded))
                : (ulong?)null;
            var returnedFrames = frames.Where(frame =>
                    (frame.PresentStartedAtUtcTicks >= windowStart && frame.PresentStartedAtUtcTicks <= nowUtcTicks)
                    || (frame.OutcomeAtUtcTicks is { } ended && ended >= windowStart && ended <= nowUtcTicks))
                .Select(frame => ToDisplayFrame(frame, measured.TryGetValue(frame, out var values) ? values : default))
                .ToArray();
            result.Add(new FrameTimingDisplayStream(key.ProcessId, key.ProcessStartKey, key.Source,
                key.SwapChain, complete, statistics, droppedCount, returnedFrames)
            {
                DisplayedTime = displayedTime,
                DisplayLatency = displayLatency,
                IncompleteReasons = complete ? [] : [.. reasons]
            });
        }

        return result;
    }

    // cpuStart of each Present = the previous Present's stop on the same stream (PresentMon 2.x).
    private Dictionary<long, long> CpuStarts(StreamKey key, long fromTicks, long throughTicks)
    {
        var result = new Dictionary<long, long>();
        if (!streams.TryGetValue(key, out var timeline) && !retiredStreams.TryGetValue(key, out timeline)) return result;
        var records = timeline.Snapshot(fromTicks, throughTicks);
        for (var index = 1; index < records.Length; index++)
        {
            if (records[index - 1].StopTicks != 0) result[records[index].Sequence] = records[index - 1].StopTicks;
        }

        return result;
    }

    private static string? Reason(DisplayFrameState frame)
    {
        if (!frame.Ambiguous && frame.Outcome is FrameTimingDisplayOutcome.Displayed or FrameTimingDisplayOutcome.Discarded)
            return null;
        if (frame.Outcome == FrameTimingDisplayOutcome.Failed) return FrameTimingReasons.PresentFailed;
        if (frame.UnsupportedReason is { } unsupported) return unsupported;
        if (frame.LostEvents) return FrameTimingReasons.EventsLost;
        if (frame.Ambiguous) return FrameTimingReasons.DisplayCorrelationAmbiguous;
        if (frame.NoParsedPath) return FrameTimingReasons.DisplayPathNotParsed;
        return FrameTimingReasons.DisplayOutcomePending;
    }

    private static FramePresentMode Mode(DisplayFrameState frame)
        => frame.ComposedFlip ? FramePresentMode.ComposedFlip
            : frame.MultiplaneOverlay ? FramePresentMode.Unknown
            : frame.LegacyFlipSeen ? FramePresentMode.HardwareLegacyFlip
            : FramePresentMode.Unknown;

    private static FrameTimingDisplayFrame ToDisplayFrame(DisplayFrameState frame, DisplayedValues values)
        => new(new DateTimeOffset(frame.PresentStartedAtUtcTicks, TimeSpan.Zero), frame.Outcome,
            frame.OutcomeAtUtcTicks is { } ended ? new DateTimeOffset(ended, TimeSpan.Zero) : null,
            frame.PresentStartQpc,
            frame.Outcome == FrameTimingDisplayOutcome.Displayed ? frame.OutcomeQpc : null,
            frame.OutcomeQpc,
            frame.QpcFrequency,
            null,
            null,
            frame.VidPnSourceId,
            frame.CompositionSurfaceLuid)
        {
            PresentMode = Mode(frame),
            UnavailableReason = Reason(frame),
            BetweenDisplayChangeMs = values.BetweenMs,
            DisplayedTimeMs = values.DisplayedMs,
            UntilDisplayedMs = values.UntilMs,
            DisplayLatencyMs = values.LatencyMs
        };

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
        foreach (var key in pendingStops.Keys.Where(key => key.ProcessId == processId).ToArray())
        {
            pendingStops.Remove(key);
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

        Prune(nowUtcTicks - Limits.Retention.Ticks);
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

        Prune(nowUtcTicks - Limits.Retention.Ticks);
        var windowStart = nowUtcTicks - window.Ticks;
        return streams
            .Select(pair => (pair.Key, Records: pair.Value.Snapshot(windowStart, nowUtcTicks)))
            .Where(item => item.Records.Length > 0)
            .OrderBy(item => item.Key.ProcessId)
            .ThenBy(item => item.Key.ProcessStartKey)
            .ThenBy(item => item.Key.Source)
            .ThenBy(item => item.Key.SwapChain)
            .Select(item => new FrameTimingPresent(item.Key.ProcessId, item.Key.ProcessStartKey,
                item.Key.Source, item.Key.SwapChain, new DateTimeOffset(item.Records[^1].StartTicks, TimeSpan.Zero)))
            .ToArray();
    }

    public IReadOnlyList<FrameIntervalSample> ReadIntervals(long afterUtcTicks, long throughUtcTicks)
    {
        if (throughUtcTicks <= afterUtcTicks)
        {
            return [];
        }

        Prune(throughUtcTicks - Limits.Retention.Ticks);
        var result = new List<FrameIntervalSample>();
        foreach (var process in streams.Concat(retiredStreams)
            .GroupBy(pair => (pair.Key.ProcessId, pair.Key.ProcessStartKey)))
        {
            // Match Read's runtime-over-kernel rule within the requested interval.
            var candidates = process.Select(pair => (pair.Key,
                Records: pair.Value.Snapshot(afterUtcTicks - Limits.Retention.Ticks, throughUtcTicks))).ToArray();
            var hasRuntime = candidates.Any(item => item.Key.Source != FramePresentSource.GraphicsKernel
                && item.Records.Length > 1 && item.Records[^1].StartTicks > afterUtcTicks);
            foreach (var (key, records) in candidates)
            {
                if (hasRuntime && key.Source == FramePresentSource.GraphicsKernel)
                {
                    continue;
                }

                for (var index = 1; index < records.Length; index++)
                {
                    var endedAt = records[index].StartTicks;
                    var durationTicks = endedAt - records[index - 1].StartTicks;
                    if (endedAt <= afterUtcTicks || durationTicks <= 0)
                    {
                        continue;
                    }

                    result.Add(new FrameIntervalSample(key.ProcessId, key.ProcessStartKey,
                        key.Source, key.SwapChain,
                        new DateTimeOffset(endedAt, TimeSpan.Zero),
                        TimeSpan.FromTicks(durationTicks).TotalMilliseconds)
                    {
                        Cpu = Cpu(records[index - 1], records[index]),
                        Gpu = MeasureGpu(key, records[index - 1], records[index]),
                        CpuUnavailableReason = Cpu(records[index - 1], records[index]) is not null ? null
                            : key.Source == FramePresentSource.GraphicsKernel ? FrameTimingReasons.GraphicsKernelHasNoPresentStop
                            : FrameTimingReasons.PresentStopNotObserved,
                        GpuUnavailableReason = MeasureGpu(key, records[index - 1], records[index]) is not null ? null
                            : GpuFrames?.UnavailableReason(key.ProcessId, key.ProcessStartKey) ?? FrameTimingReasons.GpuTierNotRequested
                    });
                }
            }
        }

        result.Sort(static (left, right) => left.EndedAt.CompareTo(right.EndedAt));
        return result;
    }

    // PresentMon 2.x: cpuStart = previous stop; busy = start - cpuStart, clamped at 0 when Presents overlap;
    // wait = this Present's time in the call.
    internal static FrameCpuTiming? Cpu(PresentRecord previous, PresentRecord current)
        => previous.StopTicks == 0 || current.StopTicks == 0
            ? null
            : new FrameCpuTiming(
                TimeSpan.FromTicks(Math.Max(0, current.StartTicks - previous.StopTicks)).TotalMilliseconds,
                TimeSpan.FromTicks(current.StopTicks - current.StartTicks).TotalMilliseconds);

    // PresentMon 2.x: latency = gpuStart - cpuStart; frame time = ready - gpuStart; wait = max(0, frame time - busy).
    internal static FrameGpuTiming? Gpu(PresentRecord previous, PresentRecord current)
    {
        if (current.Gpu is not { } gpu || previous.StopTicks == 0) return null;
        var frameTicks = Math.Max(0, gpu.ReadyTicks - gpu.GpuStartTicks);
        return new FrameGpuTiming(
            TimeSpan.FromTicks(Math.Max(0, gpu.GpuStartTicks - previous.StopTicks)).TotalMilliseconds,
            TimeSpan.FromTicks(gpu.BusyTicks).TotalMilliseconds,
            TimeSpan.FromTicks(Math.Max(0, frameTicks - gpu.BusyTicks)).TotalMilliseconds,
            TimeSpan.FromTicks(frameTicks).TotalMilliseconds,
            gpu.VideoBusyTicks is { } video ? TimeSpan.FromTicks(video).TotalMilliseconds : null);
    }

    private FrameGpuTiming? MeasureGpu(StreamKey key, PresentRecord previous, PresentRecord current)
        => GpuFrames?.UnavailableReason(key.ProcessId, key.ProcessStartKey) == FrameTimingReasons.GpuWorkNotObserved
            ? Gpu(previous, current) : null;

    private FrameTimingStream? Measure(
        StreamKey key, PresentTimeline timeline, long windowStart, long windowEnd)
    {
        var inWindow = timeline.Snapshot(windowStart, windowEnd);
        if (inWindow.Length < 2)
        {
            return null;
        }

        var intervals = new double[inWindow.Length - 1];
        var busy = new List<double>(inWindow.Length);
        var wait = new List<double>(inWindow.Length);
        var cpuFrame = new List<double>(inWindow.Length);
        var gpuLatency = new List<double>();
        var gpuBusy = new List<double>();
        var gpuWait = new List<double>();
        var gpuFrame = new List<double>();
        var videoBusy = new List<double>();
        var count = 0;
        for (var index = 1; index < inWindow.Length; index++)
        {
            var ticks = inWindow[index].StartTicks - inWindow[index - 1].StartTicks;
            if (ticks > 0)
            {
                intervals[count++] = TimeSpan.FromTicks(ticks).TotalMilliseconds;
            }

            if (Cpu(inWindow[index - 1], inWindow[index]) is { } cpu)
            {
                busy.Add(cpu.BusyMs);
                wait.Add(cpu.WaitMs);
                cpuFrame.Add(cpu.FrameTimeMs);
            }

            if (MeasureGpu(key, inWindow[index - 1], inWindow[index]) is { } gpu)
            {
                gpuLatency.Add(gpu.LatencyMs);
                gpuBusy.Add(gpu.BusyMs);
                gpuWait.Add(gpu.WaitMs);
                gpuFrame.Add(gpu.FrameTimeMs);
                if (gpu.VideoBusyMs is { } video) videoBusy.Add(video);
            }
        }

        var statistics = FrameIntervalStatistics.Compute(intervals.AsSpan(0, count));
        if (statistics is null) return null;
        var cpuStatistics = busy.Count == 0 ? null : new FrameCpuStatistics(
            FrameDurationStatistics.Compute(busy.ToArray())!,
            FrameDurationStatistics.Compute(wait.ToArray())!,
            FrameDurationStatistics.Compute(cpuFrame.ToArray())!);
        var gpuStatistics = gpuBusy.Count == 0 ? null : new FrameGpuStatistics(
            FrameDurationStatistics.Compute(gpuLatency.ToArray())!,
            FrameDurationStatistics.Compute(gpuBusy.ToArray())!,
            FrameDurationStatistics.Compute(gpuWait.ToArray())!,
            FrameDurationStatistics.Compute(gpuFrame.ToArray())!,
            FrameDurationStatistics.Compute(videoBusy.ToArray()));
        var resolutions = new List<FrameOutputResolution>(2);
        if (swapChainSizes.TryGetValue((key.ProcessId, key.ProcessStartKey, key.SwapChain), out var swapChainSize))
            resolutions.Add(swapChainSize);
        if (compositionSizes.TryGetValue(key, out var compositionSize)) resolutions.Add(compositionSize);
        return new FrameTimingStream(key.Source, key.SwapChain, new DateTimeOffset(inWindow[^1].StartTicks, TimeSpan.Zero), statistics)
        {
            PresentCount = inWindow.Length,
            Cpu = cpuStatistics,
            CpuUnavailableReason = cpuStatistics is not null ? null
                : key.Source == FramePresentSource.GraphicsKernel ? FrameTimingReasons.GraphicsKernelHasNoPresentStop
                : FrameTimingReasons.PresentStopNotObserved,
            Gpu = gpuStatistics,
            GpuUnavailableReason = gpuStatistics is not null ? null
                : GpuFrames?.UnavailableReason(key.ProcessId, key.ProcessStartKey) ?? FrameTimingReasons.GpuTierNotRequested,
            OutputResolutions = resolutions,
            OutputResolutionUnavailableReason = resolutions.Count == 0 ? FrameTimingReasons.OutputSizeNotObserved : null
        };
    }

    private void Prune(long oldestKeptTicks)
    {
        PruneDisplay(oldestKeptTicks);
        PruneTimelines(streams, oldestKeptTicks);
        PruneTimelines(retiredStreams, oldestKeptTicks);
        foreach (var key in compositionSizes.Keys.Where(key => !streams.ContainsKey(key)).ToArray())
            compositionSizes.Remove(key);
    }

    private static void PruneTimelines(Dictionary<StreamKey, PresentTimeline> timelines, long oldestKeptTicks)
    {
        foreach (var (key, timeline) in timelines.ToArray())
        {
            while (timeline.Count > 0 && timeline.FirstStart < oldestKeptTicks)
            {
                timeline.RemoveFirst();
            }

            if (timeline.Count == 0)
            {
                timelines.Remove(key);
            }
        }
    }

    private void RemoveProcess(int processId)
    {
        foreach (var key in swapChainSizes.Keys.Where(key => key.ProcessId == processId).ToArray())
            swapChainSizes.Remove(key);
        foreach (var key in streams.Keys.Where(key => key.ProcessId == processId).ToArray())
        {
            if (retiredStreams.Count < Limits.MaximumStreams)
            {
                if (retiredStreams.TryGetValue(key, out var retained))
                {
                    if (retained.AppendFrom(streams[key]) is { } lostThrough)
                        presentRateCapacityThroughUtcTicks = Math.Max(presentRateCapacityThroughUtcTicks, lostThrough);
                }
                else retiredStreams[key] = streams[key];
            }
            else if (streams[key].Count > 0)
                streamCapacityThroughUtcTicks = Math.Max(streamCapacityThroughUtcTicks, streams[key].MaxStart());
            streams.Remove(key);
        }
    }

    internal readonly record struct StreamKey(int ProcessId, long ProcessStartKey, FramePresentSource Source, ulong SwapChain);
    private readonly record struct ThreadKey(int ProcessId, long ProcessStartKey, int ThreadId);
    private readonly record struct Win32kTokenKey(ulong CompositionSurfaceLuid, ulong PresentCount, ulong BindId);
    private readonly record struct DisplayedValues(double? BetweenMs, double? DisplayedMs, double? UntilMs, double? LatencyMs);

    private sealed class DisplayFrameState(
        StreamKey stream,
        int threadId,
        long sequence,
        long presentStartedAtUtcTicks,
        long presentStartQpc,
        long qpcFrequency)
    {
        public StreamKey Stream { get; } = stream;
        public int ThreadId { get; } = threadId;
        public long Sequence { get; } = sequence;
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
        // A display path whose screen time this ledger does not parse (FrameTimingReasons value).
        public string? UnsupportedReason { get; set; }
        public bool ComposedFlip { get; set; }
        public bool MultiplaneOverlay { get; set; }
        // Any parsed path event arrived inside the Present call.
        public bool PathSeen { get; set; }
        public bool NoParsedPath { get; set; }
        public bool LostEvents { get; set; }
        public bool Ambiguous { get; set; }
    }
}

/// <summary>Identifies the Present a GPU frame belongs to, so its record can be written back when complete.</summary>
internal readonly record struct GpuFrameOwner(PresentFrameLedger Ledger, PresentFrameLedger.StreamKey Stream, long Sequence)
{
    public void Complete(PresentGpuRecord record) => Ledger.SetGpuFrame(Stream, Sequence, record);
}

/// <summary>The GPU frame tier as the ledger sees it: Present stops close frames, and it names why a process has no GPU frames.</summary>
internal interface IGpuFrameBoundary
{
    string UnavailableReason(int processId, long processStartKey);
}
