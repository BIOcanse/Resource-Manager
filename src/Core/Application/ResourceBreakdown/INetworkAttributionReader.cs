namespace ResourceManager.App.Application.ResourceBreakdown;

public interface INetworkAttributionReader
{
    NetworkAttributionSnapshot Read(NetworkAttributionReadRequest request);
}

public interface IEtwNetworkAttributionReader
{
    NetworkAttributionSnapshot Read(bool includeObservedTraffic = false);
}

public enum NetworkAttributionReadMode { Legacy, ObservedTraffic, LegacyAndObservedTraffic }

public sealed record NetworkAttributionReadRequest(
    double ReceiveBytesPerSecond,
    double SendBytesPerSecond,
    IReadOnlyDictionary<int, string> ProcessNames)
{
    public NetworkAttributionReadMode Mode { get; init; } = NetworkAttributionReadMode.Legacy;
}

