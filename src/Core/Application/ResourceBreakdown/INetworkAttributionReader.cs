namespace ResourceManager.App.Application.ResourceBreakdown;

public interface INetworkAttributionReader
{
    NetworkAttributionSnapshot Read(NetworkAttributionReadRequest request);
}

public interface IEtwNetworkAttributionReader
{
    NetworkAttributionSnapshot Read();
}

public sealed record NetworkAttributionReadRequest(
    double ReceiveBytesPerSecond,
    double SendBytesPerSecond,
    IReadOnlyDictionary<int, string> ProcessNames);

public sealed record NetworkAttributionSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<int, NetworkProcessAttribution> Processes,
    NetworkAttributionProviderState ProviderState,
    IReadOnlyDictionary<int, NetworkProcessAttribution>? RawProcesses = null)
{
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
