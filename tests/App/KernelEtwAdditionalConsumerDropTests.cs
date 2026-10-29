using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwAdditionalConsumerDropTests
{
    [Fact]
    public void NetworkCountsInvalidIncompleteFrozenAndStaleCallbacksWithoutCountingAcceptedTrafficAsDropped()
    {
        var broker = new Broker();
        var readable = true;
        using var owner = new EtwNetworkTcpIpCaptureOwner(broker, () => readable, new(), () => 1000);
        owner.Start();
        var source = broker.Source;
        void Deliver(int pid, int bytes, long tick, long generation = 1)
            => owner.ObserveBoundNetworkTransfer(source, new(generation, DateTimeOffset.UnixEpoch),
                pid, bytes, true, false, IPAddress.Loopback, tick, 100);
        Deliver(41, 4096, 1001);
        Deliver(0, 4096, 1001);
        Deliver(41, 4096, 1000);
        readable = false;
        Deliver(41, 4096, 1002);
        Deliver(41, 4096, 1003, 2);
        AssertCounts(broker, "network-transfer", 5, 4);
        foreach (var reason in new[] { "invalid-payload", "incomplete-window", "frozen", "stale-binding" })
            AssertCounts(broker, "network-transfer/rejected:" + reason, 1, 1);
        owner.Stop();
        Assert.Equal(1, broker.Releases);
    }

    [Fact]
    public void DiskCountsZeroPayloadOldProcessAndStaleBindingWithoutChangingAcceptedBuckets()
    {
        var broker = new Broker();
        using var owner = new EtwPhysicalDiskIoCaptureOwner(broker, new(), () => 1000, pid => new ProcessLease(pid));
        owner.Start();
        var source = broker.Source;
        owner.ObserveBoundDiskTransfer(source, new(1, DateTimeOffset.UnixEpoch), 41, (ulong)4096, false, 1001, 0, 101);
        owner.ObserveBoundDiskTransfer(source, new(1, DateTimeOffset.UnixEpoch), 41, (ulong)0, false, 1001, 0, 101);
        owner.ObserveBoundDiskTransfer(source, new(1, DateTimeOffset.UnixEpoch), 41, (ulong)4096, false, 1001, 0, 99);
        owner.ObserveBoundDiskTransfer(source, new(2, DateTimeOffset.UnixEpoch), 41, (ulong)4096, false, 1001, 0, 101);
        Assert.Equal(1, owner.RetainedPairCount);
        AssertCounts(broker, "disk-transfer", 4, 3);
        foreach (var reason in new[] { "invalid-payload", "before-process-birth", "stale-binding" })
            AssertCounts(broker, "disk-transfer/rejected:" + reason, 1, 1);
        owner.Stop();
        Assert.Equal(1, broker.Releases);
    }

    [Fact]
    public void RendererCountsInvalidAndInFlightCancelledInvalidations()
    {
        var broker = new Broker();
        using var cache = new RendererAbsenceCache(broker);
        using var lease = cache.Acquire();
        cache.OnProcessChanged(broker.Source, 41);
        cache.OnProcessChanged(broker.Source, 0);
        broker.Source.Cancel();
        cache.OnProcessChanged(broker.Source, 41);
        AssertCounts(broker, "renderer-invalidation", 3, 2);
        AssertCounts(broker, "renderer-invalidation/rejected:invalid-process", 1, 1);
        AssertCounts(broker, "renderer-invalidation/rejected:stale-binding", 1, 1);
    }

    private static void AssertCounts(Broker broker, string suffix, long received, long dropped)
    {
        var kind = Assert.Single(EtwEventDropDiagnostics.Read().Sessions.Single(item => item.Name == broker.Name).Kinds,
            item => item.Kind == $"holder/{broker.Id}/{suffix}");
        Assert.Equal((received, dropped), (kind.Received, kind.Dropped));
    }

    private sealed class ProcessLease(uint pid) : IWindowsNetworkEndpointProcessLease
    {
        public uint ProcessId => pid;
        public bool TryReadCreationFileTime(out ulong value) { value = 100; return true; }
        public void Dispose() { }
    }

    private sealed class Broker : IKernelEtwSessionBroker
    {
        internal string Name { get; } = "additional-consumer-drops-" + Guid.NewGuid();
        internal string Id { get; private set; } = "";
        internal KernelEtwEventSource Source { get; private set; } = null!;
        internal int Releases;
        private KernelTraceEventParser.Keywords keywords;
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            Id = request.Id;
            keywords = request.Keywords;
            var drops = EtwEventDropDiagnostics.Start(Name, () => 0);
            Source = new(request.Id, drops.NamedKind);
            request.Bind(Source, new(1, DateTimeOffset.UnixEpoch));
            Source.Activate();
            return new Lease(this, drops);
        }
        public KernelEtwSessionSnapshot GetSnapshot() => new("Running", "fixture", 1,
            DateTimeOffset.UnixEpoch, keywords, 1, 0);
        private sealed class Lease(Broker owner, EtwEventDropSession drops) : IKernelEtwSubscription
        {
            private int released;
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                owner.Source.Cancel();
                owner.Releases++;
                drops.Complete();
            }
        }
    }
}
