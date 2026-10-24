namespace ResourceManager.Shared.Runtime;

public sealed record DesktopProcessWindowObservation(
    int ProcessId, long CreationFileTimeUtc,
    bool HasVisibleWindow, bool HasBackgroundWindow, bool HasHiddenWindow)
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
    public const int MaximumWindows = 2048;
    // Covers the maximum typed JSON frame, including publisher and foreground metadata.
    public const int MaximumRequestBytes = 512 * 1024;
}
