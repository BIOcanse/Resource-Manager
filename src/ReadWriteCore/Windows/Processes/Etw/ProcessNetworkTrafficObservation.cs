namespace ResourceManager.App.Domain.ResourceBreakdown;

/// <summary>
/// TCP/UDP event sizes observed in one complete delivery window, including loopback.
/// Counts are decimal strings so JSON consumers retain the full unsigned integer.
/// This is neither a process lifetime counter nor a wire/application payload measurement.
/// </summary>
public sealed record ProcessNetworkTrafficWindow(
    DateTimeOffset CapturedAt,
    long WindowMilliseconds,
    IReadOnlyList<ProcessNetworkTrafficObservation> Processes);

public sealed record ProcessNetworkTrafficObservation(
    int ProcessId,
    string? CreationWindowsFileTime100Ns,
    string? ReceiveBytes,
    string? SendBytes,
    double? ReceiveBytesPerSecond,
    double? SendBytesPerSecond);
