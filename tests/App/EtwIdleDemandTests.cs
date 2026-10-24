using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwIdleDemandTests
{
    [Fact]
    public void UnavailableNetworkAndDiskLeasesExpireWithoutEverRunning()
    {
        var networkBroker = new DeniedBroker();
        using var network = new EtwNetworkTcpIpAttributionReader(new EtwNetworkTcpIpMonitoringZone(),
            networkBroker, NullLogger<EtwNetworkTcpIpAttributionReader>.Instance);
        network.Read();
        var diskBroker = new DeniedBroker();
        using var disk = new EtwPhysicalDiskIoAttributionReader(diskBroker,
            NullLogger<EtwPhysicalDiskIoAttributionReader>.Instance);
        disk.Read();
        Assert.Equal(0, networkBroker.Releases);
        Assert.Equal(0, diskBroker.Releases);
        network.StopIfIdle(DateTimeOffset.UtcNow.AddSeconds(10));
        disk.StopIfIdle(DateTimeOffset.UtcNow.AddSeconds(10));
        network.StopIfIdle(DateTimeOffset.UtcNow.AddSeconds(20));
        disk.StopIfIdle(DateTimeOffset.UtcNow.AddSeconds(20));
        Assert.Equal(1, networkBroker.Releases);
        Assert.Equal(1, diskBroker.Releases);
    }

    private sealed class DeniedBroker : IKernelEtwSessionBroker
    {
        internal int Releases;
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request) => new Lease(this);
        public KernelEtwSessionSnapshot GetSnapshot() => new("Unavailable", "denied", 0,
            DateTimeOffset.UnixEpoch, KernelTraceEventParser.Keywords.None, 1, 0);
        private sealed class Lease(DeniedBroker owner) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose() => owner.Releases++;
        }
    }
}
