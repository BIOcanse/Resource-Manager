namespace ResourceManager.Shared.Runtime;

// A window that is not shown is not reported: it is not tray evidence and no decision reads it.
public sealed record DesktopProcessWindowObservation(
    int ProcessId, long CreationFileTimeUtc,
    bool HasVisibleWindow, bool HasBackgroundWindow)
{
    /// <summary>
    /// The publisher could not open the process (for example a protected game) and reports it by
    /// PID only; the reader matches the PID to the instance that existed before the observation.
    /// </summary>
    public const long UnverifiedCreationTime = 0;
}

public sealed record InteractiveDesktopObservation(
    int PublisherProcessId, long PublisherCreationFileTimeUtc, int SessionId,
    long ObservedAtUtcTicks, bool Complete, bool ForegroundKnown,
    DesktopProcessWindowObservation? Foreground,
    IReadOnlyList<DesktopProcessWindowObservation> Windows)
{
    public const string EndpointPath = "/api/runtime/interactive-desktop";
    // The publisher stream: one long request whose body carries one observation per line. The stream
    // is the session's selected publisher while it is open; each line replaces the observation.
    public const string PublisherStreamPath = EndpointPath + "/publisher";
    public const int MaximumWindows = 2048;
    // Covers the maximum typed JSON frame, including publisher and foreground metadata. On the
    // publisher stream it bounds each line.
    public const int MaximumRequestBytes = 512 * 1024;
}
