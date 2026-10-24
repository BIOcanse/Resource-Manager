using ResourceManager.App.Application.NetworkTelemetry;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Domain.ResourceTable;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class IpHelperNetworkAttributionReader(
    IKnownNetworkProxyCatalog proxyCatalog,
    IMonitoringSourceZoneRegistry sourceZones,
    IEtwNetworkAttributionReader etwNetworkAttributionReader) : INetworkAttributionReader, IResourceTableProviderStateSource, IDisposable
{
    private readonly object gate = new();
    private readonly ITcpDataStats tcpDataStats = new NativeTcpDataStats();
    private readonly Func<NetworkEndpointSnapshot> endpointReader = ReadEndpoints;
    private readonly Dictionary<string, MibTcpRow> enabledTcpStats = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    internal IpHelperNetworkAttributionReader(IKnownNetworkProxyCatalog proxyCatalog, IMonitoringSourceZoneRegistry sourceZones,
        IEtwNetworkAttributionReader etwNetworkAttributionReader, ITcpDataStats tcpDataStats,
        Func<NetworkEndpointSnapshot> endpointReader)
        : this(proxyCatalog, sourceZones, etwNetworkAttributionReader)
    { this.tcpDataStats = tcpDataStats; this.endpointReader = endpointReader; }
    private Dictionary<string, TcpByteCounter> previousTcpBytes = new(StringComparer.OrdinalIgnoreCase);
    private NetworkAttributionProviderState lastProviderState = new(
        "ip-helper-network",
        "Starting",
        "IP Helper 网络归因尚未采样。");

    public NetworkAttributionSnapshot Read(NetworkAttributionReadRequest request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (request.Mode == NetworkAttributionReadMode.ObservedTraffic)
            return ReadEtwObservedTraffic();
        if (request.Mode == NetworkAttributionReadMode.LegacyAndObservedTraffic)
        {
            var etw = ReadEtwObservedTraffic();
            try { return ReadLegacy(request, etw) with { ObservedTraffic = etw.ObservedTraffic }; }
            catch (Exception error) when (error is not ObjectDisposedException)
            {
                System.Diagnostics.Trace.TraceError("IP Helper network attribution failed: {0}", error);
                lastProviderState = new("ip-helper-network", "Unavailable", "IP Helper 网络归因不可用。");
                return new(DateTimeOffset.UtcNow, new Dictionary<int, NetworkProcessAttribution>(), lastProviderState)
                    { ObservedTraffic = etw.ObservedTraffic };
            }
        }
        return ReadLegacy(request, null);
    }

    private NetworkAttributionSnapshot ReadEtwObservedTraffic()
        => sourceZones.CanRead(MonitoringSourceZoneIds.EtwNetworkTcpIp)
            ? etwNetworkAttributionReader.Read(includeObservedTraffic: true)
            : new(DateTimeOffset.UtcNow, new Dictionary<int, NetworkProcessAttribution>(),
                new("etw-network-tcpip", "Frozen", "ETW 网络事件归因当前处于功能区冻结。"));

    private NetworkAttributionSnapshot ReadLegacy(NetworkAttributionReadRequest request, NetworkAttributionSnapshot? observedEtw)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!sourceZones.CanRead(MonitoringSourceZoneIds.IpHelperNetwork))
        {
            lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); ReleaseTcpStats(); previousTcpBytes.Clear(); }
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
            ObjectDisposedException.ThrowIf(disposed, this);
            var now = DateTimeOffset.UtcNow;
            var endpointSnapshot = endpointReader();
            var endpoints = endpointSnapshot.Endpoints;
            // A closed connection owns no kernel statistics. Do not write its old tuple after it disappeared.
            // A failed table read does not prove that any connection closed.
            if (endpointSnapshot.Tcp4Complete)
            {
                var establishedKeys = endpoints.Where(static endpoint => endpoint.Tcp4Row is { State: TcpStateEstablished })
                    .Select(static endpoint => endpoint.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var key in enabledTcpStats.Keys.Where(key => !establishedKeys.Contains(key)).ToArray()) enabledTcpStats.Remove(key);
            }
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
            var etwAttribution = observedEtw ?? (sourceZones.CanRead(MonitoringSourceZoneIds.EtwNetworkTcpIp)
                ? etwNetworkAttributionReader.Read(request.Mode == NetworkAttributionReadMode.LegacyAndObservedTraffic)
                : NetworkAttributionSnapshot.NotRequested);
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
                totalReceive + totalSend,
                rawProcessValues.Values.Concat(effectiveProcessValues.Values)
                    .Any(static item => item.IsEstimated && item.ReceiveBytesPerSecond + item.SendBytesPerSecond > 0));
            lastProviderState = providerState;
            return new NetworkAttributionSnapshot(
                now,
                ToSnapshotDictionary(effectiveProcessValues),
                providerState,
                ToSnapshotDictionary(rawProcessValues))
            {
                ObservedTraffic = etwAttribution.ObservedTraffic
            };
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
