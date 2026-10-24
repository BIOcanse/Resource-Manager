namespace ResourceManager.App.Application.ResourceBreakdown;

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
            "NotRequested",
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
