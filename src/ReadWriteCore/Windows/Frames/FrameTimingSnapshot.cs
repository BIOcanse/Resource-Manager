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
}

/// <summary>Frame streams for one process instance (PID and UTC FILETIME birth).</summary>
public sealed record FrameTimingProcess(
    int ProcessId,
    long ProcessStartKey,
    IReadOnlyList<FrameTimingStream> Streams);

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
    ulong? CompositionSurfaceLuid);

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
}

/// <summary>An application Present interval identified by its ending timestamp.</summary>
public sealed record FrameIntervalSample(
    int ProcessId,
    long ProcessStartKey,
    FramePresentSource Source,
    ulong SwapChain,
    DateTimeOffset EndedAt,
    double DurationMs);

/// <summary>Intervals in the closed range (after, through], from the original shared ledger.</summary>
public sealed record FrameIntervalBatch(
    DateTimeOffset ObservedAt,
    bool Complete,
    IReadOnlyList<FrameIntervalSample> Intervals);

/// <summary>
/// Existing Host-owned Present ETW session boundary. The SDK owner borrows this
/// source; it does not open a second session or copy its ledger.
/// </summary>
public interface IFrameTimingObservationSource
{
    IDisposable AcquireSubscription();

    /// <summary>Returns null when the original shared capture is unavailable.</summary>
    FrameTimingSnapshot? Read(TimeSpan window);

    /// <summary>Returns intervals ending in the closed range (after, through]. Null means unavailable.</summary>
    FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through);
}
