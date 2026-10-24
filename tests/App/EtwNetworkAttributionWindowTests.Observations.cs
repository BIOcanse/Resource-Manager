using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class EtwNetworkAttributionWindowTests
{
    [Fact]
    public void DefaultRateReadVerifiesIdentityWithoutPublishingAnObservedWindow()
    {
        var broker = new Broker();
        long tick = 0;
        var opens = 0;
        using var reader = ObservationReader(broker, () => tick,
            pid => { opens++; return new ProcessLease(pid, 100); });
        reader.Read();
        Transfer(reader, broker, 41, 2000, true, false, IPAddress.Loopback, 1000, 100);
        tick = 2001;
        var ordinary = reader.Read();
        Assert.Equal(1000, ordinary.RawProcessValues[41].SendBytesPerSecond);
        Assert.Null(ordinary.ObservedTraffic);
        Assert.Equal(1, opens);
        Assert.Equal("2000", Assert.Single(reader.Read(includeObservedTraffic: true).ObservedTraffic!.Processes).SendBytes);
        Assert.Equal(2, opens);
        Assert.Equal(1, broker.SubscribeCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void StableCumulativeLossAllowsACompleteObservedWindowAndNewLossClearsIt(int previousLoss)
    {
        var broker = new Broker { EventsLost = previousLoss };
        long tick = 0;
        using var reader = ObservationReader(broker, () => tick, pid => new ProcessLease(pid, 100));
        reader.Read(includeObservedTraffic: true);
        Transfer(reader, broker, 41, 11, true, false, IPAddress.Loopback, 1000, 100);
        tick = 2001;
        Assert.Equal("11", Assert.Single(reader.Read(includeObservedTraffic: true).ObservedTraffic!.Processes).SendBytes);
        broker.EventsLost++;
        tick = 2100;
        Assert.Null(reader.Read(includeObservedTraffic: true).ObservedTraffic);
        Transfer(reader, broker, 41, 9000, true, false, IPAddress.Loopback, 2100, 100);
        Transfer(reader, broker, 42, 3, true, true, IPAddress.Loopback, 4100, 100);
        tick = 4101;
        var row = Assert.Single(reader.Read(includeObservedTraffic: true).ObservedTraffic!.Processes);
        Assert.Equal(42, row.ProcessId);
        Assert.Equal("3", row.SendBytes);
        Assert.Equal(1, broker.SubscribeCount);
    }

    [Fact]
    public void CompleteWindowPreservesDirectionalIntegerCountsIdentityAndLoopback()
    {
        var broker = new Broker();
        long tick = 0;
        const ulong birth = 9007199254740993;
        var lease = new ProcessLease(41, birth);
        var opens = 0;
        using var reader = ObservationReader(broker, () => tick, pid => { opens++; Assert.Equal(41u, pid); return lease; });
        Assert.Null(reader.Read(includeObservedTraffic: true).ObservedTraffic);
        Transfer(reader, broker, 41, 2049, false, false, IPAddress.IPv6Loopback, 1000, birth);
        Transfer(reader, broker, 41, 8193, true, true, IPAddress.Loopback, 1100, birth + 1);
        Transfer(reader, broker, 41, 17, true, false, IPAddress.Parse("203.0.113.1"), 1200, birth + 2);
        tick = 2001;

        var snapshot = reader.Read(includeObservedTraffic: true);
        var window = Assert.IsType<ProcessNetworkTrafficWindow>(snapshot.ObservedTraffic);
        var row = Assert.Single(window.Processes);
        Assert.Equal(2000, window.WindowMilliseconds);
        Assert.Equal(snapshot.CapturedAt, window.CapturedAt);
        Assert.Equal(birth.ToString(CultureInfo.InvariantCulture), row.CreationWindowsFileTime100Ns);
        Assert.Equal("2049", row.ReceiveBytes);
        Assert.Equal("8210", row.SendBytes);
        Assert.Equal(1024.5, row.ReceiveBytesPerSecond);
        Assert.Equal(4105, row.SendBytesPerSecond);
        Assert.Equal(8.5, snapshot.Processes[41].SendBytesPerSecond);
        Assert.Equal(4105, snapshot.RawProcessValues[41].SendBytesPerSecond);
        Assert.Equal(1, opens);
        Assert.Equal(2, lease.Reads);
        Assert.Equal(1, lease.Disposals);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(window, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var encoded = json.RootElement.GetProperty("processes")[0];
        Assert.Equal(JsonValueKind.String, encoded.GetProperty("creationWindowsFileTime100Ns").ValueKind);
        Assert.Equal(birth.ToString(CultureInfo.InvariantCulture), encoded.GetProperty("creationWindowsFileTime100Ns").GetString());
        Assert.Equal("8210", encoded.GetProperty("sendBytes").GetString());
    }

    [Fact]
    public void ReusedPidExcludesQueuedEventsBeforeTheCurrentCreationTime()
    {
        var broker = new Broker();
        long tick = 0;
        using var reader = ObservationReader(broker, () => tick, pid => new ProcessLease(pid, 200));
        reader.Read(includeObservedTraffic: true);
        Transfer(reader, broker, 41, 9000, true, false, IPAddress.Loopback, 1000, 199);
        Transfer(reader, broker, 41, 3, false, true, IPAddress.IPv6Loopback, 1100, 200);
        Transfer(reader, broker, 41, 7, true, false, IPAddress.Loopback, 1200, 201);
        tick = 2001;

        var snapshot = reader.Read(includeObservedTraffic: true);
        var row = Assert.Single(snapshot.ObservedTraffic!.Processes);

        Assert.Equal("200", row.CreationWindowsFileTime100Ns);
        Assert.Equal("3", row.ReceiveBytes);
        Assert.Equal("7", row.SendBytes);
        Assert.Equal(200UL, snapshot.RawProcessValues[41].ProcessStartKey);
        Assert.Equal(3.5, snapshot.RawProcessValues[41].SendBytesPerSecond);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("zero")]
    [InlineData("wrong-pid")]
    [InlineData("first-read")]
    [InlineData("second-read")]
    [InlineData("exited")]
    [InlineData("changed-birth")]
    [InlineData("missing-event-time")]
    [InlineData("open-throws")]
    public void UnknownIdentityIsEmptyForOnlyThatProcess(string fault)
    {
        var broker = new Broker();
        long tick = 0;
        var failed = new ProcessLease(fault == "wrong-pid" ? 100u : 41u, fault == "zero" ? 0UL : 100UL)
        {
            FailAt = fault == "first-read" ? 1 : fault == "exited" ? 2 : 0,
            ThrowAt = fault == "second-read" ? 2 : 0,
            SecondBirth = fault == "changed-birth" ? 101UL : null
        };
        var healthy = new ProcessLease(42, 200);
        using var reader = ObservationReader(broker, () => tick, pid =>
        {
            if (pid == 42) return healthy;
            if (fault == "open-throws") throw new InvalidOperationException("identity fixture");
            return fault == "denied" ? null : failed;
        });
        reader.Read(includeObservedTraffic: true);
        Transfer(reader, broker, 41, 9000, true, false, IPAddress.Loopback, 1000,
            fault == "missing-event-time" ? null : 100UL);
        Transfer(reader, broker, 42, 5, false, true, IPAddress.Loopback, 1100, 200);
        tick = 2001;

        var snapshot = reader.Read(includeObservedTraffic: true);
        var rows = snapshot.ObservedTraffic!.Processes;

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(41, snapshot.RawProcessValues.Keys);
        Assert.Equal(200UL, snapshot.RawProcessValues[42].ProcessStartKey);
        Assert.Equal(new ProcessNetworkTrafficObservation(41, null, null, null, null, null), rows[0]);
        Assert.Equal("5", rows[1].ReceiveBytes);
        Assert.Equal("0", rows[1].SendBytes);
        Assert.Equal(1, healthy.Disposals);
        Assert.Equal(fault is "denied" or "open-throws" ? 0 : 1, failed.Disposals);
    }

    [Fact]
    public void CompleteEmptyWindowIsDistinctFromAnIncompleteWindow()
    {
        var broker = new Broker();
        long tick = 0;
        var opens = 0;
        using var reader = ObservationReader(broker, () => tick, _ => { opens++; return null; });
        Assert.Null(reader.Read(includeObservedTraffic: true).ObservedTraffic);
        tick = 2000;
        Assert.Null(reader.Read(includeObservedTraffic: true).ObservedTraffic);
        tick = 2001;
        Assert.Empty(reader.Read(includeObservedTraffic: true).ObservedTraffic!.Processes);
        Assert.Equal(0, opens);
    }

    [Fact]
    public void ExactWindowUsesCapturedDeliveryBoundsAndRejectsFutureDeliveries()
    {
        var broker = new Broker();
        long tick = 0;
        using var reader = ObservationReader(broker, () => tick, pid => new ProcessLease(pid, 100));
        reader.Read(includeObservedTraffic: true);
        Transfer(reader, broker, 41, 500, true, false, IPAddress.Loopback, 1000, 100);
        Transfer(reader, broker, 41, 1, true, false, IPAddress.Loopback, 1001, 100);
        Transfer(reader, broker, 41, 2, true, false, IPAddress.Loopback, 3001, 100);
        Transfer(reader, broker, 41, 700, true, false, IPAddress.Loopback, 3002, 100);
        tick = 3001;

        Assert.Equal("3", Assert.Single(reader.Read(includeObservedTraffic: true).ObservedTraffic!.Processes).SendBytes);
    }

    [Fact]
    public void QueueLimitKeepsFullUnsignedSizesAndOverflowClearsThenRecovers()
    {
        var broker = new Broker();
        long tick = 0;
        using var reader = ObservationReader(broker, () => tick, pid => new ProcessLease(pid, 100));
        reader.Read(includeObservedTraffic: true);
        for (var index = 0; index < EtwNetworkTcpIpAttributionReader.MaximumRetainedEvents; index++)
            Transfer(reader, broker, 41, -1, true, false, IPAddress.Loopback, 1000, 100);
        tick = 2001;
        var expected = (ulong)uint.MaxValue * EtwNetworkTcpIpAttributionReader.MaximumRetainedEvents;
        Assert.Equal(expected.ToString(CultureInfo.InvariantCulture),
            Assert.Single(reader.Read(includeObservedTraffic: true).ObservedTraffic!.Processes).SendBytes);
        Transfer(reader, broker, 41, 1, true, false, IPAddress.Loopback, 2100, 100);
        tick = 2101;
        Assert.Null(reader.Read(includeObservedTraffic: true).ObservedTraffic);
        Assert.Empty(reader.Read(includeObservedTraffic: true).RawProcessValues);
        Transfer(reader, broker, 42, 8000, false, true, IPAddress.Loopback, 4100, 100);
        tick = 4101;
        var recovered = Assert.Single(reader.Read(includeObservedTraffic: true).ObservedTraffic!.Processes);
        Assert.Equal(42, recovered.ProcessId);
        Assert.Equal("8000", recovered.ReceiveBytes);
        Assert.Equal(1, broker.SubscribeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowResetDuringIdentityLookupSuppressesThePendingResult(bool loss)
    {
        var broker = new Broker();
        long tick = 0;
        var reset = true;
        var leases = new List<ProcessLease>();
        using var reader = ObservationReader(broker, () => tick, pid =>
        {
            var lease = new ProcessLease(pid, 100);
            leases.Add(lease);
            if (reset)
            {
                reset = false;
                if (loss) broker.EventsLost++;
                else { broker.Source.Cancel(); broker.Rebind(); }
            }
            return lease;
        });
        reader.Read(includeObservedTraffic: true);
        Transfer(reader, broker, 41, 5000, true, false, IPAddress.Loopback, 1000, 100);
        tick = 2001;
        var invalid = reader.Read(includeObservedTraffic: true);
        Assert.Null(invalid.ObservedTraffic);
        Assert.Empty(invalid.RawProcessValues);
        Assert.Equal(1, Assert.Single(leases).Disposals);
        Transfer(reader, broker, 42, 10, true, true, IPAddress.Loopback, 4001, 100);
        tick = 4002;
        Assert.Equal("10", Assert.Single(reader.Read(includeObservedTraffic: true).ObservedTraffic!.Processes).SendBytes);
    }

    [Fact]
    public void CleanupFailureDoesNotSkipOtherIdentityHandles()
    {
        var broker = new Broker();
        long tick = 0;
        var first = new ProcessLease(41, 100) { ThrowOnDispose = true };
        var second = new ProcessLease(42, 100);
        using var reader = ObservationReader(broker, () => tick, pid => pid == 41 ? first : second);
        reader.Read(includeObservedTraffic: true);
        Transfer(reader, broker, 41, 1, true, false, IPAddress.Loopback, 1000, 100);
        Transfer(reader, broker, 42, 2, true, false, IPAddress.Loopback, 1000, 100);
        tick = 2001;
        Assert.Equal(2, reader.Read(includeObservedTraffic: true).ObservedTraffic!.Processes.Count);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
    }

    [Fact]
    public void ActualReadOnlyProcessHandlePreservesExactBirthForDeliveredEvents()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var birth = checked((ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc());
        var broker = new Broker();
        long tick = 0;
        using var reader = ObservationReader(broker, () => tick, WindowsNetworkEndpointProcessLease.Open);
        reader.Read(includeObservedTraffic: true);
        Transfer(reader, broker, process.Id, 1025, false, true, IPAddress.Loopback, 1000, birth);
        tick = 2001;

        var row = Assert.Single(reader.Read(includeObservedTraffic: true).ObservedTraffic!.Processes);

        Assert.Equal(birth.ToString(CultureInfo.InvariantCulture), row.CreationWindowsFileTime100Ns);
        Assert.Equal("1025", row.ReceiveBytes);
        Assert.Equal(512.5, row.ReceiveBytesPerSecond);
    }

    [Fact]
    public void EventTimestampUsesFullUtcFileTimeAndRejectsAnUnrepresentableTime()
    {
        const long exact = 9007199254740993;
        Assert.Equal((ulong)exact, EtwNetworkTcpIpAttributionReader.ToEventFileTime(DateTime.FromFileTimeUtc(exact)));
        Assert.Null(EtwNetworkTcpIpAttributionReader.ToEventFileTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)));
    }

    [Fact]
    public void FrozenSourceStartsNoSessionAndPerformsNoIdentityQueries()
    {
        var broker = new Broker();
        var zone = new EtwNetworkTcpIpMonitoringZone();
        zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        using var reader = new EtwNetworkTcpIpAttributionReader(zone, broker,
            NullLogger<EtwNetworkTcpIpAttributionReader>.Instance, () => 1000,
            _ => throw new InvalidOperationException("No identity query is allowed while frozen."));

        var snapshot = reader.Read(includeObservedTraffic: true);

        Assert.Null(snapshot.ObservedTraffic);
        Assert.Empty(snapshot.RawProcessValues);
        Assert.Equal("Frozen", snapshot.ProviderState.State);
        Assert.Equal(0, broker.SubscribeCount);
    }

    [Fact]
    public void FreezeWhileIdentitiesAreBeingReadDiscardsTheWindow()
    {
        var broker = new Broker();
        var zone = new EtwNetworkTcpIpMonitoringZone();
        long tick = 0;
        var lease = new ProcessLease(41, 100);
        using var reader = new EtwNetworkTcpIpAttributionReader(zone, broker,
            NullLogger<EtwNetworkTcpIpAttributionReader>.Instance, () => tick,
            _ => { zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze); return lease; });
        reader.Read(includeObservedTraffic: true);
        Transfer(reader, broker, 41, 100, true, false, IPAddress.Loopback, 1000, 100);
        tick = 2001;

        var snapshot = reader.Read(includeObservedTraffic: true);

        Assert.Null(snapshot.ObservedTraffic);
        Assert.Empty(snapshot.RawProcessValues);
        Assert.Equal(1, lease.Disposals);
        Assert.Equal("Frozen", snapshot.ProviderState.State);
    }

    [Fact]
    public void FrozenDeliveriesInvalidateTheWindowBeforeAResumedRead()
    {
        var broker = new Broker();
        var zone = new EtwNetworkTcpIpMonitoringZone();
        long tick = 0;
        using var reader = new EtwNetworkTcpIpAttributionReader(zone, broker,
            NullLogger<EtwNetworkTcpIpAttributionReader>.Instance, () => tick,
            pid => new ProcessLease(pid, 100));
        reader.Read(includeObservedTraffic: true);
        Transfer(reader, broker, 41, 5000, true, false, IPAddress.Loopback, 1000, 100);
        zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        Transfer(reader, broker, 41, 7000, true, false, IPAddress.Loopback, 2100, 100);
        zone.ApplyMode(ResourceManagerComputeZoneMode.Normal);
        tick = 2101;
        Assert.Null(reader.Read(includeObservedTraffic: true).ObservedTraffic);
        Transfer(reader, broker, 41, 3, true, true, IPAddress.Loopback, 4100, 100);
        tick = 4101;
        Assert.Equal("3", Assert.Single(reader.Read(includeObservedTraffic: true).ObservedTraffic!.Processes).SendBytes);
    }

    [Fact]
    public async Task HostedStopReleasesTheSharedSessionLeaseAndQueuedEvents()
    {
        var broker = new Broker();
        long tick = 0;
        using var reader = ObservationReader(broker, () => tick, pid => new ProcessLease(pid, 100));
        await reader.StartAsync(CancellationToken.None);
        reader.Read(includeObservedTraffic: true);
        Transfer(reader, broker, 41, 100, true, false, IPAddress.Loopback, 1000, 100);

        await reader.StopAsync(CancellationToken.None);

        Assert.Equal(1, broker.DisposeCount);
        Assert.False(broker.Source.IsActive);
    }

    private static EtwNetworkTcpIpAttributionReader ObservationReader(Broker broker, Func<long> clock,
        Func<uint, IWindowsNetworkEndpointProcessLease?> open)
        => new(new EtwNetworkTcpIpMonitoringZone(), broker, NullLogger<EtwNetworkTcpIpAttributionReader>.Instance, clock, open);

    private static void Transfer(EtwNetworkTcpIpAttributionReader reader, Broker broker, int pid, int size,
        bool send, bool udp, IPAddress remote, long tick, ulong? fileTime)
        => reader.ObserveBoundNetworkTransfer(broker.Source, broker.Context, pid, size, send, udp, remote, tick, fileTime);

    private sealed class ProcessLease(uint pid, ulong birth) : IWindowsNetworkEndpointProcessLease
    {
        public uint ProcessId => pid;
        internal int Reads, Disposals, FailAt, ThrowAt;
        internal ulong? SecondBirth;
        internal bool ThrowOnDispose;
        public bool TryReadCreationFileTime(out ulong created)
        {
            Reads++;
            if (Reads == ThrowAt) throw new InvalidOperationException("query fixture");
            created = Reads > 1 ? SecondBirth ?? birth : birth;
            return Reads != FailAt;
        }
        public void Dispose()
        {
            Disposals++;
            if (ThrowOnDispose) throw new InvalidOperationException("release fixture");
        }
    }
}
