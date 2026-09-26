using ResourceManager.App.Application.NetworkTelemetry;
using ResourceManager.App.Application.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class IpHelperNetworkAttributionReader
{
    private static MutableNetworkAttribution GetOrAdd(
        Dictionary<int, MutableNetworkAttribution> processValues,
        int processId)
    {
        if (processValues.TryGetValue(processId, out var value))
        {
            return value;
        }

        value = new MutableNetworkAttribution();
        processValues[processId] = value;
        return value;
    }

    private static IReadOnlyDictionary<int, NetworkProcessAttribution> ToSnapshotDictionary(
        Dictionary<int, MutableNetworkAttribution> values)
    {
        return values
            .Where(static item => item.Value.TotalBytesPerSecond > 0
                || item.Value.TcpConnectionCount > 0
                || item.Value.UdpSocketCount > 0
                || item.Value.ActivityEventCount > 0)
            .ToDictionary(
                static item => item.Key,
                static item => item.Value.ToSnapshot(item.Key));
    }

    private static void AddEndpointWeight(
        Dictionary<int, MutableNetworkAttribution> processValues,
        NetworkEndpoint endpoint,
        bool isKnownProxy)
    {
        var value = GetOrAdd(processValues, endpoint.ProcessId);
        if (endpoint.Protocol == NetworkEndpointProtocols.Tcp)
        {
            value.TcpConnectionCount++;
        }
        else
        {
            value.UdpSocketCount++;
        }

        value.EstimateWeight += endpoint.EstimateWeight;
        value.IsKnownProxy |= isKnownProxy;
    }

    private static void AddEndpointBytes(
        Dictionary<int, MutableNetworkAttribution> processValues,
        int processId,
        double receiveBytesPerSecond,
        double sendBytesPerSecond)
    {
        var value = GetOrAdd(processValues, processId);
        value.ReceiveBytesPerSecond += receiveBytesPerSecond;
        value.SendBytesPerSecond += sendBytesPerSecond;
    }

    private (double ReceiveBytesPerSecond, double SendBytesPerSecond) MergeEtwAttribution(
        Dictionary<int, MutableNetworkAttribution> processValues,
        IReadOnlyDictionary<int, NetworkProcessAttribution> etwValues,
        IReadOnlyDictionary<int, string> processNames)
    {
        var addedReceive = 0d;
        var addedSend = 0d;
        foreach (var item in etwValues)
        {
            var source = item.Value;
            if (source.ProcessId <= 0)
            {
                continue;
            }

            var target = GetOrAdd(processValues, source.ProcessId);
            var receiveDelta = Math.Max(0, source.ReceiveBytesPerSecond - target.ReceiveBytesPerSecond);
            var sendDelta = Math.Max(0, source.SendBytesPerSecond - target.SendBytesPerSecond);
            if (receiveDelta > 0 || sendDelta > 0)
            {
                target.ReceiveBytesPerSecond += receiveDelta;
                target.SendBytesPerSecond += sendDelta;
                target.IsEstimated = false;
                addedReceive += receiveDelta;
                addedSend += sendDelta;
            }

            target.ActivityEventCount += source.ActivityEventCount;
            target.IsKnownProxy |= proxyCatalog.IsKnownProxyProcessName(
                GetProcessName(source.ProcessId, processNames));
        }

        return (addedReceive, addedSend);
    }

    private void RedistributeKnownProxyEgress(
        Dictionary<int, MutableNetworkAttribution> effectiveProcessValues,
        IReadOnlyList<ObservedNetworkEndpoint> observations)
    {
        var proxyProcessIds = effectiveProcessValues
            .Where(static item => item.Value.IsKnownProxy && item.Value.TotalBytesPerSecond > 0)
            .Select(static item => item.Key)
            .ToHashSet();
        if (proxyProcessIds.Count == 0)
        {
            return;
        }

        var clients = observations
            .Where(item => !proxyProcessIds.Contains(item.Endpoint.ProcessId)
                && item.Endpoint.IsHostLocal
                && (proxyCatalog.IsLikelyProxyPort(item.Endpoint.LocalPort)
                    || proxyCatalog.IsLikelyProxyPort(item.Endpoint.RemotePort)))
            .GroupBy(static item => item.Endpoint.ProcessId)
            .Select(static group => new
            {
                ProcessId = group.Key,
                Weight = group.Sum(static item => item.Weight)
            })
            .Where(static item => item.Weight > 0)
            .ToArray();
        var clientWeight = clients.Sum(static item => item.Weight);
        if (clientWeight <= 0)
        {
            return;
        }

        foreach (var proxyProcessId in proxyProcessIds)
        {
            if (!effectiveProcessValues.TryGetValue(proxyProcessId, out var proxyValue)
                || proxyValue.TotalBytesPerSecond <= 0)
            {
                continue;
            }

            foreach (var client in clients)
            {
                var share = client.Weight / clientWeight;
                var target = GetOrAdd(effectiveProcessValues, client.ProcessId);
                target.ReceiveBytesPerSecond += proxyValue.ReceiveBytesPerSecond * share;
                target.SendBytesPerSecond += proxyValue.SendBytesPerSecond * share;
                target.IsEstimated = true;
            }

            proxyValue.ReceiveBytesPerSecond = 0;
            proxyValue.SendBytesPerSecond = 0;
            proxyValue.IsEstimated = true;
        }
    }

    private static void DistributeEstimatedResidual(
        IEnumerable<MutableNetworkAttribution> values,
        double receiveResidual,
        double sendResidual)
    {
        if (receiveResidual <= 0 && sendResidual <= 0)
        {
            return;
        }

        var candidates = values
            .Where(static value => value.EstimateWeight > 0)
            .ToArray();
        var totalWeight = candidates.Sum(static value => value.EstimateWeight);
        if (totalWeight <= 0)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            var share = candidate.EstimateWeight / totalWeight;
            candidate.ReceiveBytesPerSecond += receiveResidual * share;
            candidate.SendBytesPerSecond += sendResidual * share;
            candidate.IsEstimated = true;
        }
    }

    private static NetworkAttributionProviderState ResolveProviderState(
        int endpointCount,
        int exactTcpConnections,
        int etwActivityEvents,
        double systemBytesPerSecond)
    {
        if (exactTcpConnections > 0 || etwActivityEvents > 0)
        {
            return new NetworkAttributionProviderState(
                "ip-helper-network",
                "Active",
                "网络进程归因可用。");
        }

        if (endpointCount > 0)
        {
            return new NetworkAttributionProviderState(
                "ip-helper-network",
                "Degraded",
                "IP Helper 已识别 TCP/UDP owner PID，但当前没有可用 TCP EStats 字节差分；网络速率按活动 endpoint 权重估算，外部网络口径会排除本机环回 endpoint。");
        }

        return systemBytesPerSecond > 0
            ? new NetworkAttributionProviderState(
                "ip-helper-network",
                "Degraded",
                "系统存在网络流量，但当前 IP Helper 连接表没有可归因 endpoint。")
            : new NetworkAttributionProviderState(
                "ip-helper-network",
                "Active",
                "IP Helper 连接表可用，当前没有网络 endpoint。");
    }

    private static string GetProcessName(
        int processId,
        IReadOnlyDictionary<int, string> processNames)
    {
        return processNames.GetValueOrDefault(processId) ?? string.Empty;
    }
}
