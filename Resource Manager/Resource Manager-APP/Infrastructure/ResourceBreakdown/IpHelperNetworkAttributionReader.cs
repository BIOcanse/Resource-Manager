using ResourceManager.App.Application.NetworkTelemetry;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Domain.ResourceTable;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class IpHelperNetworkAttributionReader(
    IKnownNetworkProxyCatalog proxyCatalog,
    IMonitoringSourceZoneRegistry sourceZones,
    IEtwNetworkAttributionReader etwNetworkAttributionReader) : INetworkAttributionReader, IResourceTableProviderStateSource
{
    private readonly object gate = new();
    private Dictionary<string, TcpByteCounter> previousTcpBytes = new(StringComparer.OrdinalIgnoreCase);
    private NetworkAttributionProviderState lastProviderState = new(
        "ip-helper-network",
        "Starting",
        "IP Helper 网络归因尚未采样。");

    public NetworkAttributionSnapshot Read(NetworkAttributionReadRequest request)
    {
        if (!sourceZones.CanRead(MonitoringSourceZoneIds.IpHelperNetwork))
        {
            lastProviderState = new NetworkAttributionProviderState(
                "ip-helper-network",
                "Frozen",
                "IP Helper 网络归因监控源当前处于功能区冻结。");
            return new NetworkAttributionSnapshot(
                DateTimeOffset.UtcNow,
                new Dictionary<int, NetworkProcessAttribution>(),
                lastProviderState);
        }

        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var endpoints = ReadEndpoints();
            var rawProcessValues = new Dictionary<int, MutableNetworkAttribution>();
            var effectiveProcessValues = new Dictionary<int, MutableNetworkAttribution>();
            var processNames = request.ProcessNames;
            var observations = new List<ObservedNetworkEndpoint>(endpoints.Count);
            var currentTcpBytes = new Dictionary<string, TcpByteCounter>(StringComparer.OrdinalIgnoreCase);
            var exactTcpConnections = 0;
            var exactReceiveBytesPerSecond = 0d;
            var exactSendBytesPerSecond = 0d;
            var exactExternalReceiveBytesPerSecond = 0d;
            var exactExternalSendBytesPerSecond = 0d;

            foreach (var endpoint in endpoints)
            {
                if (endpoint.ProcessId <= 0)
                {
                    continue;
                }

                var processName = GetProcessName(endpoint.ProcessId, processNames);
                var isKnownProxy = proxyCatalog.IsKnownProxyProcessName(processName);
                AddEndpointWeight(rawProcessValues, endpoint, isKnownProxy);
                if (endpoint.LeavesHost)
                {
                    AddEndpointWeight(effectiveProcessValues, endpoint, isKnownProxy);
                }

                if (endpoint.Tcp4Row is not { } tcpRow || !TryReadTcpBytes(endpoint.Key, tcpRow, now, currentTcpBytes, out var receive, out var send))
                {
                    observations.Add(new ObservedNetworkEndpoint(endpoint, 0, 0, HasExactBytes: false, isKnownProxy));
                    continue;
                }

                exactTcpConnections++;
                AddEndpointBytes(rawProcessValues, endpoint.ProcessId, receive, send);
                exactReceiveBytesPerSecond += receive;
                exactSendBytesPerSecond += send;
                if (endpoint.LeavesHost)
                {
                    AddEndpointBytes(effectiveProcessValues, endpoint.ProcessId, receive, send);
                    exactExternalReceiveBytesPerSecond += receive;
                    exactExternalSendBytesPerSecond += send;
                }

                observations.Add(new ObservedNetworkEndpoint(endpoint, receive, send, HasExactBytes: true, isKnownProxy));
            }

            previousTcpBytes = currentTcpBytes;
            var etwAttribution = sourceZones.CanRead(MonitoringSourceZoneIds.EtwNetworkTcpIp)
                ? etwNetworkAttributionReader.Read()
                : NetworkAttributionSnapshot.NotRequested;
            var rawEtwAdded = MergeEtwAttribution(
                rawProcessValues,
                etwAttribution.RawProcessValues,
                processNames);
            var effectiveEtwAdded = MergeEtwAttribution(
                effectiveProcessValues,
                etwAttribution.EffectiveProcesses,
                processNames);
            exactReceiveBytesPerSecond += rawEtwAdded.ReceiveBytesPerSecond;
            exactSendBytesPerSecond += rawEtwAdded.SendBytesPerSecond;
            exactExternalReceiveBytesPerSecond += effectiveEtwAdded.ReceiveBytesPerSecond;
            exactExternalSendBytesPerSecond += effectiveEtwAdded.SendBytesPerSecond;

            var totalReceive = Math.Max(0, request.ReceiveBytesPerSecond);
            var totalSend = Math.Max(0, request.SendBytesPerSecond);
            var receiveResidual = Math.Max(0, totalReceive - exactReceiveBytesPerSecond);
            var sendResidual = Math.Max(0, totalSend - exactSendBytesPerSecond);
            var effectiveReceiveResidual = Math.Max(0, totalReceive - exactExternalReceiveBytesPerSecond);
            var effectiveSendResidual = Math.Max(0, totalSend - exactExternalSendBytesPerSecond);
            DistributeEstimatedResidual(rawProcessValues.Values, receiveResidual, sendResidual);
            DistributeEstimatedResidual(effectiveProcessValues.Values, effectiveReceiveResidual, effectiveSendResidual);
            RedistributeKnownProxyEgress(effectiveProcessValues, observations);

            var providerState = ResolveProviderState(
                endpoints.Count,
                exactTcpConnections,
                etwAttribution.EffectiveProcesses.Values.Sum(static item => item.ActivityEventCount),
                totalReceive + totalSend);
            lastProviderState = providerState;
            return new NetworkAttributionSnapshot(
                now,
                ToSnapshotDictionary(effectiveProcessValues),
                providerState,
                ToSnapshotDictionary(rawProcessValues));
        }
    }

    public IReadOnlyList<ResourceTableProviderState> GetStates(IReadOnlyList<ResourceTableColumn> columns)
    {
        if (!columns.Any(static column => column.Id.Equals(ResourceTableColumnIds.Network, StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var state = lastProviderState;
        return state.State.Equals("Active", StringComparison.OrdinalIgnoreCase)
            ? []
            :
            [
                new ResourceTableProviderState(state.Id, state.State, state.Message)
            ];
    }
}
