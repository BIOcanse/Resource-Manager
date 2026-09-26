namespace ResourceManager.App.Application.NetworkTelemetry;

public interface IKnownNetworkProxyCatalog
{
    bool IsKnownProxyProcessName(string? processName);

    bool IsLikelyProxyPort(int port);
}
