using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwIdleDemandTests
{
    [Fact]
    public void NetworkAndDiskReadsDoNotAcquireSessionLeases()
    {
        var networkBroker = new DeniedBroker();
        using var network = new EtwNetworkTcpIpAttributionReader(networkBroker, NullLogger<EtwNetworkTcpIpAttributionReader>.Instance);
        var diskBroker = new DeniedBroker();
        using var disk = new EtwPhysicalDiskIoAttributionReader(diskBroker,
            NullLogger<EtwPhysicalDiskIoAttributionReader>.Instance);
        for (var index = 0; index < 3; index++)
        {
            Assert.Empty(network.Read(includeObservedTraffic: true).RawProcessValues);
            Assert.Empty(disk.Read(includeObservedTraffic: true).Processes);
        }
        Assert.Equal(0, networkBroker.Acquisitions);
        Assert.Equal(0, diskBroker.Acquisitions);
    }

    [Fact]
    public async Task UnavailableNetworkAndDiskLeasesRemainHeldUntilTheirHostedRunsStop()
    {
        var networkBroker = new DeniedBroker();
        using var network = new EtwNetworkTcpIpAttributionReader(networkBroker, NullLogger<EtwNetworkTcpIpAttributionReader>.Instance);
        var diskBroker = new DeniedBroker();
        using var disk = new EtwPhysicalDiskIoAttributionReader(diskBroker,
            NullLogger<EtwPhysicalDiskIoAttributionReader>.Instance);
        await network.StartAsync(CancellationToken.None);
        await disk.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref networkBroker.Acquisitions) == 1
                && Volatile.Read(ref diskBroker.Acquisitions) == 1, TimeSpan.FromSeconds(5)));
            Assert.Equal(0, networkBroker.Releases);
            Assert.Equal(0, diskBroker.Releases);
            network.Read();
            disk.Read();
            Assert.Equal(1, networkBroker.Acquisitions);
            Assert.Equal(KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.NetworkTCPIP,
                Assert.Single(networkBroker.RequestedKeywords));
            Assert.Equal(1, diskBroker.Acquisitions);
        }
        finally
        {
            await network.StopAsync(CancellationToken.None);
            await disk.StopAsync(CancellationToken.None);
        }
        Assert.Equal(1, networkBroker.Releases);
        Assert.Equal(1, diskBroker.Releases);
        network.Read();
        disk.Read();
        network.Dispose();
        disk.Dispose();
        Assert.Equal(1, networkBroker.Acquisitions);
        Assert.Equal(1, diskBroker.Acquisitions);
        Assert.Equal(1, networkBroker.Releases);
        Assert.Equal(1, diskBroker.Releases);
    }

    private sealed class DeniedBroker : IKernelEtwSessionBroker
    {
        internal int Releases;
        internal int Acquisitions;
        internal System.Collections.Concurrent.ConcurrentQueue<KernelTraceEventParser.Keywords> RequestedKeywords { get; } = new();
        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            RequestedKeywords.Enqueue(request.Keywords);
            Interlocked.Increment(ref Acquisitions);
            return new Lease(this);
        }
        public KernelEtwSessionSnapshot GetSnapshot() => new("Unavailable", "denied", 0,
            DateTimeOffset.UnixEpoch, KernelTraceEventParser.Keywords.None, Acquisitions - Releases, 0);
        private sealed class Lease(DeniedBroker owner) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            private int disposed;
            public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Increment(ref owner.Releases); }
        }
    }
}
