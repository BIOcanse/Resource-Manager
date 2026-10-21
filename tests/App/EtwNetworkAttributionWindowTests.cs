using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed partial class EtwNetworkAttributionWindowTests
{
    [Fact]
    public void EventLossDiscardsOldRatesAndRecoversWithAStableNonzeroCumulativeCount()
    {
        var broker = new Broker();
        long tick = 0;
        using var reader = NewReader(broker, () => tick);
        Assert.Equal("Warming", reader.Read().ProviderState.State);
        Observe(reader, broker, 41, 4000, 1000);
        tick = 2001;
        Assert.Equal(2000, reader.Read().RawProcessValues[41].SendBytesPerSecond);

        broker.EventsLost = 1;
        tick = 2100;
        var incomplete = reader.Read();
        Assert.Equal("Warming", incomplete.ProviderState.State);
        Assert.Empty(incomplete.RawProcessValues);
        Assert.Empty(incomplete.Processes);
        Assert.Null(incomplete.ObservedTraffic);
        Observe(reader, broker, 99, 8000, 2000); // A delivery waiting across the reset must be rejected.
        Observe(reader, broker, 42, 8192, 4100);
        tick = 4201;
        var recovered = reader.Read();
        Assert.Equal("Running", recovered.ProviderState.State);
        var row = Assert.Single(recovered.RawProcessValues);
        Assert.Equal(42, row.Key);
        Assert.Equal(4096, row.Value.SendBytesPerSecond);
        Assert.Equal(4096, recovered.Processes[42].SendBytesPerSecond);
        tick = 4300;
        Assert.Equal(4096, reader.Read().RawProcessValues[42].SendBytesPerSecond);
        Assert.Equal(1, broker.SubscribeCount);
    }

    [Fact]
    public void LossBetweenSnapshotChecksSuppressesTheResultBeingBuilt()
    {
        var broker = new Broker();
        long tick = 0;
        using var reader = NewReader(broker, () => tick);
        reader.Read();
        Observe(reader, broker, 41, 4000, 1500);
        // The second query in this read captures the pre-build broker state.
        var loseAfter = broker.SnapshotCount + 2;
        broker.AfterSnapshot = count => { if (count == loseAfter) broker.EventsLost = 1; };
        tick = 2500;

        var result = reader.Read();

        Assert.Empty(result.RawProcessValues);
        Assert.Empty(result.Processes);
        Assert.Null(result.ObservedTraffic);
        Assert.Equal("Warming", result.ProviderState.State);
        Assert.Equal(1, broker.SubscribeCount);
    }

    [Fact]
    public void ExistingCumulativeLossStartsANewWindowRatherThanPermanentlyDisablingRates()
    {
        var broker = new Broker { EventsLost = 5 };
        long tick = 1000;
        using var reader = NewReader(broker, () => tick);
        Assert.Equal("Warming", reader.Read().ProviderState.State);
        Observe(reader, broker, 41, 4000, 3000);
        tick = 3001;

        var snapshot = reader.Read();

        Assert.Equal("Running", snapshot.ProviderState.State);
        Assert.Equal(2000, snapshot.RawProcessValues[41].SendBytesPerSecond);
        Assert.Equal(1, broker.SubscribeCount);
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
    public void DisposalReleasesALeaseStartedByReadAndPreventsRestart()
    {
        var broker = new Broker();
        var reader = NewReader(broker, () => 1000);
        reader.Read();
        reader.Dispose();
        reader.Dispose();

        Assert.Equal(1, broker.DisposeCount);
        Assert.False(broker.Source.IsActive);
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
        Assert.Equal(1, broker.SubscribeCount);
    }

    private static EtwNetworkTcpIpAttributionReader NewReader(Broker broker, Func<long> clock)
        => new(new EtwNetworkTcpIpMonitoringZone(), broker,
            NullLogger<EtwNetworkTcpIpAttributionReader>.Instance, clock);

    private static void Observe(EtwNetworkTcpIpAttributionReader reader, Broker broker, int pid, int bytes, long tick)
        => reader.ObserveBoundNetworkTransfer(broker.Source, broker.Context, pid, bytes, true, false,
            IPAddress.Parse("203.0.113.1"), tick);

    private sealed class Broker : IKernelEtwSessionBroker
    {
        internal KernelEtwSubscriptionRequest? Request;
        internal KernelEtwSessionContext Context { get; } = new(61, DateTimeOffset.UnixEpoch);
        internal KernelEtwEventSource Source { get; private set; } = new();
        internal int EventsLost;
        internal int SubscribeCount;
        internal int DisposeCount;
        internal int SnapshotCount;
        internal Action<int>? AfterSnapshot;

        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            SubscribeCount++;
            Request = request;
            Rebind();
            return new Lease(this);
        }

        internal void Rebind()
        {
            Source = new KernelEtwEventSource();
            Request!.Bind(Source, Context);
            Source.Activate();
        }

        public KernelEtwSessionSnapshot GetSnapshot()
        {
            var snapshot = new KernelEtwSessionSnapshot("Running", "fixture", Context.Generation,
                Context.StartedAt, KernelTraceEventParser.Keywords.NetworkTCPIP, 1 - DisposeCount, EventsLost);
            AfterSnapshot?.Invoke(++SnapshotCount);
            return snapshot;
        }

        private sealed class Lease(Broker owner) : IKernelEtwSubscription
        {
            private bool disposed;
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                owner.DisposeCount++;
                owner.Source.Cancel();
            }
        }
    }
}
