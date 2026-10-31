namespace ResourceManager.ReadWrite.Windows.Frames;

/// <summary>Observed application Present stream source. Values retain the original ETW meaning.</summary>
public enum FramePresentSource
{
    Dxgi,
    Direct3D9,
    GraphicsKernel
}

/// <summary>One stream keyed by the original process instance, event source and swapchain.</summary>
public sealed record FrameTimingStream(
    FramePresentSource Source,
    ulong SwapChain,
    DateTimeOffset LastPresentAt,
    FrameIntervalStatistics Statistics)
{
    public int PresentCount { get; init; }

    /// <summary>CPU busy / wait / frame time over the same window, by the PresentMon cpuStart rule. Null with <see cref="CpuUnavailableReason"/>.</summary>
    public FrameCpuStatistics? Cpu { get; init; }

    /// <summary>One of <see cref="FrameTimingReasons"/> when <see cref="Cpu"/> is null.</summary>
    public string? CpuUnavailableReason { get; init; }

    /// <summary>GPU frame statistics; only while a consumer holds the GPU frame tier. Null with <see cref="GpuUnavailableReason"/>.</summary>
    public FrameGpuStatistics? Gpu { get; init; }

    /// <summary>One of <see cref="FrameTimingReasons"/> when <see cref="Gpu"/> is null.</summary>
    public string? GpuUnavailableReason { get; init; }

    /// <summary>Latest output (swap chain / composition target) size per source; only while a consumer requests output resolution.</summary>
    public IReadOnlyList<FrameOutputResolution> OutputResolutions { get; init; } = [];
    public string? OutputResolutionUnavailableReason { get; init; }
}

/// <summary>Frame streams for one process instance (PID and UTC FILETIME birth).</summary>
public sealed record FrameTimingProcess(
    int ProcessId,
    long ProcessStartKey,
    IReadOnlyList<FrameTimingStream> Streams)
{
    /// <summary>The injected section names a process/window, not a swap chain: keep its size at that scope.</summary>
    public IReadOnlyList<FrameOutputResolution> OutputResolutions { get; init; } = [];
}

/// <summary>Latest observed PresentStart in the requested window, including a one-event stream.</summary>
public sealed record FrameTimingPresent(
    int ProcessId,
    long ProcessStartKey,
    FramePresentSource Source,
    ulong SwapChain,
    DateTimeOffset LastPresentAt);

/// <summary>A rolling observation over the exact requested window. Complete is false after event loss.</summary>
public sealed record FrameTimingSnapshot(
    DateTimeOffset ObservedAt,
    TimeSpan Window,
    bool Complete,
    IReadOnlyList<FrameTimingProcess> Processes)
{
    public IReadOnlyList<FrameTimingPresent> LastPresents { get; init; } = [];

    /// <summary>Actual display outcomes correlated by the original ETW source and ledger.</summary>
    public IReadOnlyList<FrameTimingDisplayStream> DisplayStreams { get; init; } = [];

    /// <summary>Why <see cref="Complete"/> is false: values from <see cref="FrameTimingReasons"/>. Empty when complete.</summary>
    public IReadOnlyList<string> IncompleteReasons { get; init; } = [];
}

/// <summary>Terminal state of one runtime Present for the supported display correlation paths.</summary>
public enum FrameTimingDisplayOutcome
{
    Unknown,
    Displayed,
    Discarded,
    Retired,
    Failed
}

/// <summary>
/// One original runtime Present and its display-source provenance. QPC values are source ticks;
/// only <see cref="QpcFrequency"/> converts their intervals to milliseconds.
/// </summary>
public sealed record FrameTimingDisplayFrame(
    DateTimeOffset PresentStartedAt,
    FrameTimingDisplayOutcome Outcome,
    DateTimeOffset? OutcomeAt,
    long PresentStartQpc,
    long? DisplayedQpc,
    long? OutcomeQpc,
    long QpcFrequency,
    ulong? AdapterLuid,
    string? MonitorDevicePath,
    uint? VidPnSourceId,
    ulong? CompositionSurfaceLuid)
{
    /// <summary>Display path the ledger identified for this Present.</summary>
    public FramePresentMode PresentMode { get; init; }

    /// <summary>Why no Displayed/Discarded outcome is known; a <see cref="FrameTimingReasons"/> value, null once known.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>Screen time minus the previous displayed frame's screen time (PresentMon MsBetweenDisplayChange).</summary>
    public double? BetweenDisplayChangeMs { get; init; }

    /// <summary>Next displayed frame's screen time minus this one (PresentMon MsDisplayedTime).</summary>
    public double? DisplayedTimeMs { get; init; }

    /// <summary>Screen time minus this Present's start (PresentMon MsUntilDisplayed).</summary>
    public double? UntilDisplayedMs { get; init; }

    /// <summary>Screen time minus cpuStart, the previous Present's stop on this stream (PresentMon MsDisplayLatency).</summary>
    public double? DisplayLatencyMs { get; init; }
}

/// <summary>
/// Closed-window display measurements for one exact original process/source/swapchain stream.
/// Statistics and drop counts are null while the retained stream contains unknown or incomplete outcomes.
/// </summary>
public sealed record FrameTimingDisplayStream(
    int ProcessId,
    long ProcessStartKey,
    FramePresentSource Source,
    ulong SwapChain,
    bool Complete,
    FrameIntervalStatistics? Statistics,
    ulong? DroppedCount,
    IReadOnlyList<FrameTimingDisplayFrame> Frames)
{
    public const string QpcClockSource = "TraceEventSource.QPC";

    /// <summary>Statistics of <see cref="FrameTimingDisplayFrame.DisplayedTimeMs"/>; null while incomplete.</summary>
    public FrameDurationStatistics? DisplayedTime { get; init; }

    /// <summary>Statistics of <see cref="FrameTimingDisplayFrame.DisplayLatencyMs"/>; null while incomplete or before CPU start is known.</summary>
    public FrameDurationStatistics? DisplayLatency { get; init; }

    /// <summary>Why <see cref="Complete"/> is false: distinct <see cref="FrameTimingReasons"/> values.</summary>
    public IReadOnlyList<string> IncompleteReasons { get; init; } = [];
}

/// <summary>An application Present interval identified by its ending timestamp.</summary>
public sealed record FrameIntervalSample(
    int ProcessId,
    long ProcessStartKey,
    FramePresentSource Source,
    ulong SwapChain,
    DateTimeOffset EndedAt,
    double DurationMs)
{
    /// <summary>CPU timing of the Present that ends this interval; null until its stop is observed or when the previous stop is unknown.</summary>
    public FrameCpuTiming? Cpu { get; init; }

    /// <summary>GPU timing of the frame this Present closes; only while the GPU frame tier is held.</summary>
    public FrameGpuTiming? Gpu { get; init; }

    public string? CpuUnavailableReason { get; init; }
    public string? GpuUnavailableReason { get; init; }
}

/// <summary>Intervals in the closed range (after, through], from the original shared ledger.</summary>
public sealed record FrameIntervalBatch(
    DateTimeOffset ObservedAt,
    bool Complete,
    IReadOnlyList<FrameIntervalSample> Intervals)
{
    /// <summary>Why <see cref="Complete"/> is false: values from <see cref="FrameTimingReasons"/>.</summary>
    public IReadOnlyList<string> IncompleteReasons { get; init; } = [];
}

/// <summary>
/// CPU side of one application Present by the PresentMon 2.x rule: cpuStart is the previous Present's
/// stop on the same stream; Busy = PresentStart - cpuStart (0 when the two Presents overlap);
/// Wait = time inside the Present call (PresentMon MsCPUWait, the same value as MsInPresentAPI);
/// FrameTime = Busy + Wait.
/// </summary>
public sealed record FrameCpuTiming(double BusyMs, double WaitMs)
{
    public double FrameTimeMs => BusyMs + WaitMs;
    public double TimeInPresentMs => WaitMs;
}

/// <summary>
/// GPU side of one frame by the PresentMon 2.x rule: Latency = gpuStart - cpuStart; Busy = time at least
/// one of the frame's packets ran on a non-video engine; FrameTime = ready - gpuStart;
/// Wait = max(0, FrameTime - Busy); VideoBusy = the same as Busy on video engines, null when the
/// frame ran nothing on an engine known to be video.
/// </summary>
public sealed record FrameGpuTiming(double LatencyMs, double BusyMs, double WaitMs, double FrameTimeMs, double? VideoBusyMs);

/// <summary>Window statistics of <see cref="FrameCpuTiming"/>, computed by <see cref="FrameDurationStatistics"/>.</summary>
public sealed record FrameCpuStatistics(
    FrameDurationStatistics Busy,
    FrameDurationStatistics Wait,
    FrameDurationStatistics FrameTime)
{
    public FrameDurationStatistics TimeInPresent => Wait;
}

/// <summary>Window statistics of <see cref="FrameGpuTiming"/>, computed by <see cref="FrameDurationStatistics"/>.</summary>
public sealed record FrameGpuStatistics(
    FrameDurationStatistics Latency,
    FrameDurationStatistics Busy,
    FrameDurationStatistics Wait,
    FrameDurationStatistics FrameTime,
    FrameDurationStatistics? VideoBusy);

/// <summary>Display path of one Present, named as in PresentMon's PresentMode table.</summary>
public enum FramePresentMode
{
    Unknown,
    HardwareLegacyFlip,
    ComposedFlip,
    HardwareComposedIndependentFlip
}

/// <summary>Where an output size came from.</summary>
public enum FrameOutputResolutionSource
{
    /// <summary>DXGI SwapChain_Start / DC_Start (10/12) Width/Height, then ResizeBuffers_Stop (46) NewWidth/NewHeight.</summary>
    DxgiSwapChain,
    /// <summary>Win32k TokenCompositionSurfaceObject (201) DestWidth/DestHeight; buffer or composition-target size is unverified.</summary>
    Win32kCompositionToken,
    /// <summary>The injected overlay module's shared section (swapChainWidth/Height), written on every Present it hooks.</summary>
    InjectedSharedSection
}

public sealed record FrameOutputResolution(FrameOutputResolutionSource Source, int Width, int Height, DateTimeOffset ObservedAt);

/// <summary>Optional detail tiers of a frame lease. Present intervals and CPU timing are always included.</summary>
[Flags]
public enum FrameDetail
{
    None = 0,
    /// <summary>DXGI swap chain create/resize events and the injected section size.</summary>
    OutputResolution = 1,
    /// <summary>DxgKrnl DMA and queue packet tracking: machine-wide and costly, opened only while requested.</summary>
    GpuFrames = 2
}

/// <summary>
/// What one frame lease needs. TargetProcessIds null means every process (no PID filter). Otherwise the
/// source filters DXGI/D3D9 Present by PID while the union of every lease's targets stays within the
/// declared process filter cap, and tracks GPU packets only for those processes.
/// </summary>
public sealed record FrameSubscriptionRequest(IReadOnlyList<int>? TargetProcessIds, FrameDetail Detail)
{
    public static FrameSubscriptionRequest AllProcesses { get; } = new(null, FrameDetail.None);
}

/// <summary>A frame lease whose request can change (targets start and exit) without dropping the lease.</summary>
public interface IFrameSubscriptionLease : IDisposable
{
    void Update(FrameSubscriptionRequest request);
}

/// <summary>Concrete reasons carried by frame values. The strings are wire values: add, never rename.</summary>
public static class FrameTimingReasons
{
    public const string EventsLost = "frame-events-lost";
    public const string StreamCapacity = "frame-ledger-stream-capacity";
    public const string PresentRateCapacity = "frame-ledger-present-rate-capacity";
    public const string PresentStopNotObserved = "present-stop-not-observed";
    public const string GraphicsKernelHasNoPresentStop = "graphics-kernel-stream-has-no-present-stop";
    public const string DisplayOutcomePending = "display-outcome-pending";
    public const string DisplayCorrelationAmbiguous = "display-correlation-ambiguous";
    public const string MultiplaneOverlayNotParsed = "display-path-multiplane-overlay-not-parsed";
    public const string TestPresentNotDisplayed = "dxgi-test-present-not-displayed";
    public const string LegacyFlipWithoutMmioNotParsed = "display-path-legacy-flip-without-mmio-not-parsed";
    public const string ComposedScreenTimeNotParsed = "display-path-composed-flip-screen-time-not-parsed";
    public const string DisplayPathNotParsed = "display-path-independent-flip-blit-or-dwm-not-parsed";
    public const string PresentFailed = "present-failed";
    public const string DisplayCapacity = "display-ledger-capacity";
    public const string QpcFrequencyMismatch = "qpc-frequency-mismatch";
    public const string GpuTierNotRequested = "gpu-frame-tier-not-requested";
    public const string GpuEtwProviderSessionLimit = "etw-provider-session-limit";
    public const string GpuEtwEnableFailed = "gpu-frame-tier-enable-failed";
    public const string GpuHardwareScheduledQueue = "gpu-frame-hardware-scheduled-queue-not-parsed";
    public const string GpuTrackerCapacity = "gpu-frame-tracker-capacity";
    public const string GpuWorkNotObserved = "gpu-frame-work-not-observed";
    public const string GpuPacketSequenceIncomplete = "gpu-frame-packet-sequence-incomplete";
    public const string OutputSizeNotObserved = "output-resolution-swap-chain-or-token-not-observed";
}

/// <summary>
/// Existing Host-owned Present ETW session boundary. The SDK owner borrows this
/// source; it does not open a second session or copy its ledger.
/// </summary>
public interface IFrameTimingObservationSource
{
    /// <summary>Whole-machine Present lease without optional tiers.</summary>
    IDisposable AcquireSubscription() => AcquireSubscription(FrameSubscriptionRequest.AllProcesses);

    IFrameSubscriptionLease AcquireSubscription(FrameSubscriptionRequest request);

    /// <summary>How long the ledger keeps Presents: the largest window Read and ReadIntervals accept.</summary>
    TimeSpan Retention { get; }

    /// <summary>Returns null when the original shared capture is unavailable.</summary>
    FrameTimingSnapshot? Read(TimeSpan window);

    /// <summary>Returns intervals ending in the closed range (after, through]. Null means unavailable.</summary>
    FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through);
}
