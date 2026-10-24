namespace ResourceManager.App.Application.ResourceBreakdown;

public static class NetworkAttributionProviderStates
{
    public const string Active = "active";
    public const string Degraded = "degraded";
    public const string Unavailable = "unavailable";
    public const string NotRequested = "not_requested";
    public const string Frozen = "frozen";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Idle = "idle";
    public const string Warming = "warming";
    public const string Available = "available";
    public const string Stopped = "stopped";
    public const string StopFailed = "stop_failed";
    public const string Failed = "failed";
    public const string Unsupported = "unsupported";

    public static string FromKernelEtwState(string state) => state switch
    {
        "Running" => Running,
        "Idle" => Idle,
        "Starting" => Starting,
        "Stopped" => Stopped,
        "StopFailed" => StopFailed,
        "Available" => Available,
        "Unavailable" => Unavailable,
        _ => throw new InvalidDataException($"Unknown kernel ETW state '{state}'.")
    };
}

public sealed record NetworkAttributionSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<int, NetworkProcessAttribution> Processes,
    NetworkAttributionProviderState ProviderState,
    IReadOnlyDictionary<int, NetworkProcessAttribution>? RawProcesses = null)
{
    // The independent ETW source window is never rebuilt from merged/estimated rates.
    public Domain.ResourceBreakdown.ProcessNetworkTrafficWindow? ObservedTraffic { get; init; }

    public IReadOnlyDictionary<int, NetworkProcessAttribution> EffectiveProcesses => Processes;

    public IReadOnlyDictionary<int, NetworkProcessAttribution> RawProcessValues => RawProcesses ?? Processes;

    public static NetworkAttributionSnapshot NotRequested { get; } = new(
        DateTimeOffset.UtcNow,
        new Dictionary<int, NetworkProcessAttribution>(),
        new NetworkAttributionProviderState(
            "ip-helper-network",
            NetworkAttributionProviderStates.NotRequested,
            "当前快照未请求网络进程归因。"));
}

public sealed record NetworkProcessAttribution(
    int ProcessId,
    double ReceiveBytesPerSecond,
    double SendBytesPerSecond,
    int TcpConnectionCount,
    int UdpSocketCount,
    bool IsEstimated,
    bool IsKnownProxy = false,
    int ActivityEventCount = 0)
{
    public ulong? ProcessStartKey { get; init; }
    public double TotalBytesPerSecond => ReceiveBytesPerSecond + SendBytesPerSecond;
}

public sealed record NetworkAttributionProviderState(
    string Id,
    string State,
    string Message)
{
    public Domain.Metrics.SamplingObservationStatus ObservationStatus =>
        AttributionProviderObservationStatus.Resolve(State);
}
