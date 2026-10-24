using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.NetworkTelemetry;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using Row = ResourceManager.App.Infrastructure.ResourceBreakdown.IpHelperNetworkAttributionReader.MibTcpRow;
using Data = ResourceManager.App.Infrastructure.ResourceBreakdown.IpHelperNetworkAttributionReader.TcpEstatsDataRodV0;
using Endpoint = ResourceManager.App.Infrastructure.ResourceBreakdown.IpHelperNetworkAttributionReader.NetworkEndpoint;

namespace Resource_Manager_APP.Tests;

public sealed class TcpEStatsLifecycleTests
{
    [Fact]
    public void QuietExactTcpBaselineDoesNotHideEstimatedUdpResidual()
    {
        using var reader = Create(new Stats { Enabled = true }, new Zones(),
            () => [Connection, Endpoint.Udp("udp", 456, false, true, 5000)]);
        var snapshot = reader.Read(Request);
        Assert.Contains(snapshot.Processes.Values, row => row.IsEstimated && row.TotalBytesPerSecond > 0);
        Assert.Equal("Degraded", snapshot.ProviderState.State);
    }

    private static readonly NetworkAttributionReadRequest Request = new(1000, 1000, new Dictionary<int, string> { [123] = "fixture" });
    private static readonly Endpoint Connection = Endpoint.Tcp("tcp4:fixture", 123, 3, new Row(5, 1, 2, 3, 4), false, true, 80, 443);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingCollectionIsReadWithoutTakingOwnershipAndOwnCollectionIsEnabledOnlyOnce(bool alreadyEnabled)
    {
        var source = new Stats { Enabled = alreadyEnabled };
        var zones = new Zones();
        var reader = Create(source, zones, () => [Connection]);
        reader.Read(Request);
        source.Incoming = 2048;
        reader.Read(Request);
        reader.Read(Request);
        Assert.Equal(alreadyEnabled ? 0 : 1, source.Writes.Count);
        if (!alreadyEnabled) Assert.True(source.Writes[0]);
        reader.Dispose();
        Assert.Equal(alreadyEnabled ? Array.Empty<bool>() : new[] { true, false }, source.Writes);
        Assert.Equal(alreadyEnabled, source.Enabled);
        Assert.Throws<ObjectDisposedException>(() => reader.Read(Request));
    }

    [Fact]
    public void DisabledOrUnconfirmedCollectionDoesNotTurnUndefinedCountersIntoExactBytes()
    {
        var source = new Stats { RejectEnable = true, Incoming = ulong.MaxValue };
        using var reader = Create(source, new Zones(), () => [Connection]);
        var result = reader.Read(Request);
        Assert.True(result.Processes[123].IsEstimated);
        Assert.True(double.IsFinite(result.Processes[123].ReceiveBytesPerSecond));
        Assert.True(result.Processes[123].ReceiveBytesPerSecond <= Request.ReceiveBytesPerSecond);
        Assert.Equal(new[] { true }, source.Writes);
    }

    [Fact]
    public void FreezeReleasesOwnedStatisticsAndAResumedReadStartsANewCounterWindow()
    {
        var source = new Stats();
        var zones = new Zones();
        using var reader = Create(source, zones, () => [Connection]);
        reader.Read(Request);
        zones.Enabled = false;
        Assert.Equal("Frozen", reader.Read(Request).ProviderState.State);
        Assert.Equal(new[] { true, false }, source.Writes);
        zones.Enabled = true;
        source.Incoming = 5000;
        var first = reader.Read(Request with { SendBytesPerSecond = 0 });
        // The fresh connection window has no measured delta. Existing residual estimation remains explicit.
        Assert.True(first.Processes[123].IsEstimated);
        Assert.Equal(new[] { true, false, true }, source.Writes);
    }

    [Fact]
    public void DisappearedConnectionIsForgottenWithoutWritingAnOldTuple()
    {
        var source = new Stats();
        IReadOnlyList<Endpoint> connections = [Connection];
        var reader = Create(source, new Zones(), () => connections);
        reader.Read(Request);
        connections = [];
        reader.Read(Request);
        reader.Dispose();
        Assert.Equal(new[] { true }, source.Writes);
    }

    [Fact]
    public void IncompleteEndpointEnumerationKeepsOwnershipForLaterRelease()
    {
        var source = new Stats();
        var snapshot = new IpHelperNetworkAttributionReader.NetworkEndpointSnapshot([Connection], true);
        var reader = new IpHelperNetworkAttributionReader(new Proxy(), new Zones(), null!, source, () => snapshot);
        reader.Read(Request);
        snapshot = new([], false);
        reader.Read(Request);
        reader.Dispose();
        Assert.Equal(new[] { true, false }, source.Writes);
        Assert.False(source.Enabled);
    }

    [Fact]
    public void FailedReadsDoNotAttemptToEnableUnknownState()
    {
        var source = new Stats { ReadSucceeds = false };
        using var reader = Create(source, new Zones(), () => [Connection]);
        reader.Read(Request);
        Assert.Empty(source.Writes);
    }

    [Fact]
    public void EstimatesCannotRewriteTheIndependentEtwWindowAndUnavailableDoesNotRetainIt()
    {
        var window = new ProcessNetworkTrafficWindow(DateTimeOffset.UnixEpoch, 2000,
            [new(123, "9007199254740993", "3", "7", 1.5, 3.5)]);
        var etw = new EtwReader
        {
            Snapshot = new(DateTimeOffset.UtcNow,
                new Dictionary<int, NetworkProcessAttribution> { [123] = new(123, 1.5, 3.5, 0, 0, false) },
                new("etw-network-tcpip", "Running", "fixture")) { ObservedTraffic = window }
        };
        var zones = new Zones { EtwEnabled = true };
        using var reader = new IpHelperNetworkAttributionReader(new Proxy(), zones, etw,
            new Stats { RejectEnable = true }, () => new([Connection], true));

        Assert.Null(reader.Read(Request).ObservedTraffic);
        var selectedRequest = Request with { Mode = NetworkAttributionReadMode.LegacyAndObservedTraffic };
        var merged = reader.Read(selectedRequest);

        Assert.True(merged.RawProcessValues[123].IsEstimated);
        Assert.Equal(1000, merged.RawProcessValues[123].ReceiveBytesPerSecond);
        Assert.Same(window, merged.ObservedTraffic);
        Assert.Equal(DateTimeOffset.UnixEpoch, merged.ObservedTraffic!.CapturedAt);
        Assert.Equal("3", merged.ObservedTraffic!.Processes[0].ReceiveBytes);
        Assert.Equal("7", merged.ObservedTraffic.Processes[0].SendBytes);
        etw.Snapshot = new(DateTimeOffset.UtcNow, new Dictionary<int, NetworkProcessAttribution>(),
            new("etw-network-tcpip", "Warming", "fixture"));
        Assert.Null(reader.Read(selectedRequest).ObservedTraffic);
        zones.EtwEnabled = false;
        Assert.Null(reader.Read(selectedRequest).ObservedTraffic);
        Assert.Equal(new[] { false, true, true }, etw.Requests);
    }

    private sealed class EtwReader : IEtwNetworkAttributionReader
    {
        internal required NetworkAttributionSnapshot Snapshot;
        internal List<bool> Requests { get; } = [];
        public NetworkAttributionSnapshot Read(bool includeObservedTraffic = false)
        {
            Requests.Add(includeObservedTraffic);
            return includeObservedTraffic ? Snapshot : Snapshot with { ObservedTraffic = null };
        }
    }

    [Fact]
    public void ObservedOnlyModeUsesEtwWithoutIpHelperEndpointsOrEstats()
    {
        var etw = new EtwReader { Snapshot = new(DateTimeOffset.UtcNow, new Dictionary<int, NetworkProcessAttribution>(),
            new("etw-network-tcpip", "Running", "fixture")) { ObservedTraffic = new(DateTimeOffset.UtcNow, 2000, []) } };
        var zones = new Zones { EtwEnabled = true, IpHelperEnabled = false };
        var stats = new Stats();
        using var reader = new IpHelperNetworkAttributionReader(new Proxy(), zones, etw, stats,
            () => throw new InvalidOperationException("No IP Helper endpoint query is allowed."));
        var observed = reader.Read(Request with { Mode = NetworkAttributionReadMode.ObservedTraffic });
        Assert.Same(etw.Snapshot, observed);
        Assert.Equal(new[] { true }, etw.Requests);
        Assert.Empty(stats.Writes);
        zones.EtwEnabled = false;
        Assert.Null(reader.Read(Request with { Mode = NetworkAttributionReadMode.ObservedTraffic }).ObservedTraffic);
        Assert.Equal(new[] { true }, etw.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedModeKeepsEtwFieldsWhenIpHelperIsFrozenOrFails(bool failure)
    {
        var etw = new EtwReader { Snapshot = new(DateTimeOffset.UtcNow, new Dictionary<int, NetworkProcessAttribution>(),
            new("etw-network-tcpip", "Running", "fixture")) { ObservedTraffic = new(DateTimeOffset.UtcNow, 2000, []) } };
        using var reader = new IpHelperNetworkAttributionReader(new Proxy(),
            new Zones { EtwEnabled = true, IpHelperEnabled = failure }, etw, new Stats(),
            () => throw new InvalidOperationException("private endpoint failure"));

        var result = reader.Read(Request with { Mode = NetworkAttributionReadMode.LegacyAndObservedTraffic });

        Assert.Same(etw.Snapshot.ObservedTraffic, result.ObservedTraffic);
        Assert.Equal(new[] { true }, etw.Requests);
        Assert.Empty(result.RawProcessValues);
        Assert.Equal(failure ? "Unavailable" : "Frozen", result.ProviderState.State);
        Assert.DoesNotContain("private endpoint failure", result.ProviderState.Message);
    }

    private static IpHelperNetworkAttributionReader Create(Stats stats, Zones zones, Func<IReadOnlyList<Endpoint>> endpoints)
        => new(new Proxy(), zones, null!, stats, () => new(endpoints(), true));

    private sealed class Stats : IpHelperNetworkAttributionReader.ITcpDataStats
    {
        public bool Enabled;
        public bool RejectEnable;
        public bool ReadSucceeds = true;
        public ulong Incoming;
        public List<bool> Writes { get; } = [];
        public bool Read(Row row, out bool enabled, out Data data)
        { enabled = Enabled; data = new Data { DataBytesIn = Incoming, DataBytesOut = 4096 }; return ReadSucceeds; }
        public bool SetEnabled(Row row, bool enabled)
        { Writes.Add(enabled); if (enabled && RejectEnable) return false; Enabled = enabled; return true; }
    }
    private sealed class Proxy : IKnownNetworkProxyCatalog
    {
        public bool IsKnownProxyProcessName(string? name) => false;
        public bool IsLikelyProxyPort(int port) => false;
    }
    private sealed class Zones : IMonitoringSourceZoneRegistry
    {
        public bool Enabled = true;
        public bool EtwEnabled;
        public bool IpHelperEnabled = true;
        public event Action? Changed { add { } remove { } }
        public long Version => 0;
        public bool CanRead(string id) => Enabled && (IpHelperEnabled && id == MonitoringSourceZoneIds.IpHelperNetwork
            || EtwEnabled && id == MonitoringSourceZoneIds.EtwNetworkTcpIp);
        public bool IsLowPower(string id) => false;
        public ResourceManagerComputeZoneMode GetMode(string id) => throw new NotSupportedException();
        public IResourceManagerSelfComputeZone GetZone(string id) => throw new NotSupportedException();
        public IReadOnlyList<IResourceManagerSelfComputeZone> GetZones() => [];
        public IReadOnlyList<string> GetSourceIds() => [];
        public bool Register(IResourceManagerSelfComputeZone zone) => throw new NotSupportedException();
        public bool Unregister(string id) => throw new NotSupportedException();
    }
}
