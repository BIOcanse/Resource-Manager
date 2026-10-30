using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed partial class OracleNetworkAttributionWindowTests
{
    [Fact]
    public void EventLossDiscardsOldRatesAndRecoversWithAStableNonzeroCumulativeCount()
    {
        var broker = new Broker();
        long tick = 0;
        using var reader = NewReader(broker, () => tick);
        Assert.Equal(NetworkAttributionProviderStates.Warming, reader.Read().ProviderState.State);
        Observe(reader, broker, 41, 4000, 1000);
        tick = 2001;
        Assert.Equal(2000, reader.Read().RawProcessValues[41].SendBytesPerSecond);

        broker.EventsLost = 1;
        tick = 2100;
        var incomplete = reader.Read();
        Assert.Equal(NetworkAttributionProviderStates.Warming, incomplete.ProviderState.State);
        Assert.Empty(incomplete.RawProcessValues);
        Assert.Empty(incomplete.Processes);
        Assert.Null(incomplete.ObservedTraffic);
        Observe(reader, broker, 99, 8000, 2000); // A delivery waiting across the reset must be rejected.
        Observe(reader, broker, 42, 8192, 4100);
        tick = 4201;
        var recovered = reader.Read();
        Assert.Equal(NetworkAttributionProviderStates.Running, recovered.ProviderState.State);
        var row = Assert.Single(recovered.RawProcessValues);
        Assert.Equal(42, row.Key);
        Assert.Equal(4096, row.Value.SendBytesPerSecond);
        Assert.Equal(4096, recovered.Processes[42].SendBytesPerSecond);
        tick = 4300;
        Assert.Equal(4096, reader.Read().RawProcessValues[42].SendBytesPerSecond);
        Assert.Equal(2, broker.SubscribeCount);
        Assert.Equal(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.NetworkTCPIP, broker.Keywords);
    }

    [Fact]
    public void LossBetweenSnapshotChecksIsStampedAndNextFullWindowRecovers()
    {
        var broker = new Broker();
        long tick = 0;
        using var reader = NewReader(broker, () => tick);
        reader.Read();
        Observe(reader, broker, 41, 4000, 1500);
        // Lose an event after the pre-build value is captured, so the final
        // snapshot sees a changed cumulative loss count in this same read.
        broker.AfterNextSnapshot = () => broker.EventsLost = 1;
        tick = 2500;

        var result = reader.Read(includeObservedTraffic: true);

        Assert.Empty(result.RawProcessValues);
        Assert.Empty(result.Processes);
        Assert.Null(result.ObservedTraffic);
        Assert.Equal(NetworkAttributionProviderStates.Warming, result.ProviderState.State);
        Assert.Equal(2, broker.SubscribeCount);

        var stillIncomplete = reader.Read(includeObservedTraffic: true);
        Assert.Empty(stillIncomplete.RawProcessValues);
        Assert.Null(stillIncomplete.ObservedTraffic);
        Observe(reader, broker, 99, 8000, 2000); // Already queued before the loss was stamped.
        Observe(reader, broker, 42, 8192, 4500);
        tick = 4501;
        var recovered = reader.Read(includeObservedTraffic: true);
        var row = Assert.Single(recovered.RawProcessValues);
        Assert.Equal(42, row.Key);
        Assert.Equal(4096, row.Value.SendBytesPerSecond);
        Assert.Equal(NetworkAttributionProviderStates.Running, recovered.ProviderState.State);
        Assert.NotNull(recovered.ObservedTraffic);
        Assert.Equal(2, broker.SubscribeCount);
    }

    [Fact]
    public void ExistingCumulativeLossStartsANewWindowRatherThanPermanentlyDisablingRates()
    {
        var broker = new Broker { EventsLost = 5 };
        long tick = 1000;
        using var reader = NewReader(broker, () => tick);
        Assert.Equal(NetworkAttributionProviderStates.Warming, reader.Read().ProviderState.State);
        Observe(reader, broker, 41, 4000, 3000);
        tick = 3001;

        var snapshot = reader.Read();

        Assert.Equal(NetworkAttributionProviderStates.Running, snapshot.ProviderState.State);
        Assert.Equal(2000, snapshot.RawProcessValues[41].SendBytesPerSecond);
        Assert.Equal(2, broker.SubscribeCount);
    }

    [Fact]
    public void CancelledBindingCannotWriteIntoAReplacementInTheSameGeneration()
    {
        var broker = new Broker();
        long tick = 0;
        using var reader = NewReader(broker, () => tick);
        reader.Read();
        var previous = broker.Source;
        Observe(reader, broker, 41, 4000, 1000);
        previous.Cancel();
        broker.Rebind();
        reader.ObserveBoundNetworkTransfer(previous, broker.Context, 99, 8000, true, false, IPAddress.Loopback, 1100);
        Observe(reader, broker, 42, 8192, 1100);
        tick = 2001;

        var row = Assert.Single(reader.Read().RawProcessValues);

        Assert.Equal(42, row.Key);
        Assert.Equal(4096, row.Value.SendBytesPerSecond);
    }

    [Fact]
    public void SnapshotExcludesDeliveriesAfterItsCapturedTimestamp()
    {
        var broker = new Broker();
        long tick = 1000;
        using var reader = NewReader(broker, () => tick);
        reader.Read();
        Observe(reader, broker, 41, 4000, 4002);
        tick = 3001;
        Assert.Empty(reader.Read().RawProcessValues);
        tick = 4002;
        Assert.Equal(2000, reader.Read().RawProcessValues[41].SendBytesPerSecond);
    }

    [Fact]
    public void DisposalReleasesTheHostedLeaseAndPreventsRestart()
    {
        var broker = new Broker();
        var reader = NewReader(broker, () => 1000);
        reader.Read();
        reader.Dispose();
        reader.Dispose();

        Assert.Equal(2, broker.DisposeCount);
        Assert.False(broker.Source.IsActive);
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
        Assert.Equal(2, broker.SubscribeCount);
    }

    private static OracleNetworkTcpIpAttributionReader NewReader(Broker broker, Func<long> clock)
        => StartReader(new(broker,
            NullLogger<OracleNetworkTcpIpAttributionReader>.Instance, clock, pid => new ProcessLease(pid, 100)), broker);

    private static OracleNetworkTcpIpAttributionReader StartReader(OracleNetworkTcpIpAttributionReader reader, Broker broker, int expectedAcquisitions = 2)
    {
        reader.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref broker.SubscribeCount) == expectedAcquisitions,
            TimeSpan.FromSeconds(5)), "Hosted network reader did not acquire its session lease.");
        // Start holds the capture gate until binding and activation are complete.
        reader.Read();
        return reader;
    }

    private static void Observe(OracleNetworkTcpIpAttributionReader reader, Broker broker, int pid, int bytes, long tick)
        => reader.ObserveBoundNetworkTransfer(broker.Source, broker.Context, pid, bytes, true, false,
            IPAddress.Parse("203.0.113.1"), tick, 100);

    private sealed class Broker : IOracleKernelEtwSessionBroker
    {
        private readonly object gate = new();
        private readonly Dictionary<long, Hold> active = [];
        private long next;
        internal KernelEtwSessionContext Context { get; } = new(61, DateTimeOffset.UnixEpoch);
        // Transfer deliveries use the data hold's source, never the metadata binding.
        internal OracleKernelEtwEventSource Source { get; private set; } = new();
        internal int EventsLost, SubscribeCount, DisposeCount;
        internal Action? AfterNextSnapshot;
        internal KernelTraceEventParser.Keywords Keywords
        { get { lock (gate) return active.Values.Aggregate(KernelTraceEventParser.Keywords.None, static (all, hold) => all | hold.Request.Keywords); } }
        public IOracleKernelEtwSubscription Acquire(OracleKernelEtwSubscriptionRequest request)
        {
            Hold hold;
            lock (gate)
            {
                hold = new(++next, request, new());
                active.Add(hold.Id, hold);
            }
            Bind(hold);
            Interlocked.Increment(ref SubscribeCount);
            return new Lease(this, hold);
        }
        private void Bind(Hold hold)
        {
            if (hold.Request.Keywords.HasFlag(KernelTraceEventParser.Keywords.NetworkTCPIP)) Source = hold.Source;
            hold.Request.Bind(hold.Source, Context);
            hold.Source.Activate();
        }
        internal void Rebind()
        {
            Hold[] holds;
            lock (gate) holds = active.Values.ToArray();
            foreach (var hold in holds) { hold.Source.Cancel(); hold.Source = new(); Bind(hold); }
        }
        public KernelEtwSessionSnapshot GetSnapshot()
        {
            KernelEtwSessionSnapshot snapshot;
            lock (gate) snapshot = new(active.Count == 0 ? "Idle" : "Running", "fixture", Context.Generation,
                Context.StartedAt, Keywords, active.Count, EventsLost);
            var afterSnapshot = AfterNextSnapshot;
            AfterNextSnapshot = null;
            afterSnapshot?.Invoke();
            return snapshot;
        }
        private sealed class Hold(long id, OracleKernelEtwSubscriptionRequest request, OracleKernelEtwEventSource source)
        {
            internal readonly long Id = id;
            internal readonly OracleKernelEtwSubscriptionRequest Request = request;
            internal OracleKernelEtwEventSource Source = source;
        }
        private sealed class Lease(Broker owner, Hold hold) : IOracleKernelEtwSubscription
        {
            private int disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                lock (owner.gate) { hold.Source.Cancel(); owner.active.Remove(hold.Id); Interlocked.Increment(ref owner.DisposeCount); }
            }
        }
    }
}
