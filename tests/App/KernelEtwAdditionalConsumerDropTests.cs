using System.Buffers.Binary;
using Microsoft.Diagnostics.Tracing.Parsers;
using Resource_Manager_APP.Tests.KernelEtwFixtures;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwAdditionalConsumerDropTests
{
    // The surviving historical callback matrices (network4/3, disk4/3, renderer3/2
    // and every reason) live in admission_counters.zig. A native flow has no
    // captured managed callback to invoke with a false generation after return.
    // These tests exercise raw production input and the actual return boundary.
    [Fact]
    public void NetworkCountsRawInvalidAndIncompleteEventsAndStopsAdmissionWhenItsHoldReturns()
    {
        using var broker = new Broker(KernelEtwNativeOutputs.Network);
        using var owner = new EtwNetworkTcpIpCaptureOwner(broker, new(), static () => 1000);
        owner.Start();
        broker.Network(1001, 41, 4096);
        broker.Network(1001, 0, 4096);
        broker.Network(1000, 41, 4096);
        AssertCounts(broker, "network-transfer", 3, 2);
        AssertCounts(broker, "network-transfer/rejected:invalid-payload", 1, 1);
        AssertCounts(broker, "network-transfer/rejected:incomplete-window", 1, 1);
        owner.Stop();
        Assert.Equal(1, broker.Releases);
        broker.Network(1002, 41, 4096);
        broker.Network(1003, 41, 4096);
        AssertCounts(broker, "network-transfer", 3, 2);
        AssertCounts(broker, "network-transfer/rejected:stale-binding", 0, 0);
        AssertPhysical(broker, "TcpIpSend", 5, 0);
        owner.Stop();
        Assert.Equal(1, broker.Releases);
    }

    [Fact]
    public void DiskCountsRawZeroPayloadAndPreBirthEventsWithoutChangingTheAcceptedWindow()
    {
        using var broker = new Broker(KernelEtwNativeOutputs.Disk);
        using var owner = new EtwPhysicalDiskIoCaptureOwner(broker, new(), static () => 1000);
        owner.Start();
        broker.Disk(1001, 41, 4096);
        broker.Disk(1001, 41, 0);
        broker.Disk(1002, 42, 4096); // PID42's verified birth is later than this real event timestamp.
        broker.Resource.Publish(3000);
        var row = Assert.Single(owner.Read(true).Processes);
        Assert.Equal(41, row.Key);
        Assert.Equal(1, row.Value.ReadEventCount);
        Assert.Equal(2048d, row.Value.ReadBytesPerSecond);
        AssertCounts(broker, "disk-transfer", 3, 2);
        AssertCounts(broker, "disk-transfer/rejected:invalid-payload", 1, 1);
        AssertCounts(broker, "disk-transfer/rejected:before-process-birth", 1, 1);
        owner.Stop();
        Assert.Equal(1, broker.Releases);
        broker.Disk(3001, 41, 4096);
        AssertCounts(broker, "disk-transfer", 3, 2);
        AssertCounts(broker, "disk-transfer/rejected:stale-binding", 0, 0);
        AssertPhysical(broker, "DiskIORead", 4, 0);
    }

    [Fact]
    public void RendererCountsRawInvalidationsAndStopsAdmissionWhenItsLastObservationReturns()
    {
        using var broker = new Broker(KernelEtwNativeOutputs.Renderer);
        using var cache = new RendererAbsenceCache(broker);
        using var observationRun = cache.Acquire();
        var identity = new GpuPlacementProcessInstance(41, 100, "renderer.exe", @"C:\Apps\renderer.exe");
        var stamp = cache.Read(identity);
        cache.PublishAbsent(identity, stamp);
        Assert.True(cache.Read(identity).IsAbsent);
        broker.Resource.RawProcess(1, 41, 0, "renderer.exe", 100);
        broker.Resource.Publish(1);
        cache.PublishAbsent(identity, stamp);
        Assert.False(cache.Read(identity).IsAbsent);
        broker.Resource.RawProcess(2, 0, 0, "invalid.exe");
        broker.Resource.Publish(2);
        AssertCounts(broker, "renderer-invalidation", 2, 1);
        AssertCounts(broker, "renderer-invalidation/rejected:invalid-process", 1, 1);
        observationRun.Dispose();
        Assert.Equal(1, broker.Releases);
        broker.Resource.RawProcess(3, 41, 0, "renderer.exe", 100);
        broker.Resource.Publish(3);
        AssertCounts(broker, "renderer-invalidation", 2, 1);
        AssertCounts(broker, "renderer-invalidation/rejected:stale-binding", 0, 0);
        AssertPhysical(broker, "ProcessStartGroup", 3, 0);
        Assert.False(cache.Read(identity).CanPublishAbsent);
    }

    private static void AssertCounts(Broker broker, string suffix, long received, long dropped)
        => AssertKind(broker, $"holder/{broker.Id}/{suffix}", received, dropped);
    private static void AssertPhysical(Broker broker, string name, long received, long dropped)
        => AssertKind(broker, name, received, dropped);
    private static void AssertKind(Broker broker, string name, long received, long dropped)
    {
        var kind = Assert.Single(EtwEventDropDiagnostics.Read().Sessions.Single(item => item.Name == broker.Resource.DiagnosticName).Kinds,
            item => item.Kind == name);
        Assert.Equal((received, dropped), (kind.Received, kind.Dropped));
    }

    private sealed class Broker : IKernelEtwSessionBroker, IDisposable
    {
        internal NativeKernelEtwTestResource Resource { get; } = new(NativeKernelEtwTestResource.SmallConfiguration(qpcFrequency: 1000));
        private readonly IKernelEtwSubscription sustain;
        internal string Id { get; private set; } = "";
        internal int Releases;
        internal Broker(KernelEtwNativeOutputs outputs)
        {
            Resource.Identities.Add(41, new(100));
            Resource.Identities.Add(42, new(checked((ulong)DateTimeOffset.UnixEpoch.AddDays(1).ToFileTime())));
            var keywords = outputs switch
            {
                KernelEtwNativeOutputs.Network => KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Thread,
                KernelEtwNativeOutputs.Disk => KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread,
                _ => KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ImageLoad
            };
            sustain = Resource.Acquire(new("independent-flow-owner", keywords, static (_, _) => { }) { NativeOutputs = outputs });
        }
        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            Id = request.Id;
            var actual = Resource.Acquire(request);
            if ((request.NativeOutputs & KernelEtwNativeOutputs.Renderer) != 0) Resource.Publish(0);
            return new Lease(this, actual);
        }
        public KernelEtwSessionSnapshot GetSnapshot() => Resource.GetSnapshot();
        internal void Network(long qpc, uint pid, uint bytes)
        {
            Span<byte> payload = stackalloc byte[16];
            payload.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(payload, pid);
            BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], bytes);
            payload[8] = 127; payload[11] = 1;
            Resource.Raw(new("9a280ac0-c8e0-11d1-84e2-00c04fb998a2"), 10, 1, 8, qpc, 0, pid, 0, payload);
        }
        internal void Disk(long qpc, uint pid, uint bytes)
        {
            Span<byte> payload = stackalloc byte[48];
            payload.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], bytes);
            Resource.Raw(new("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c"), 10, 2, 8, qpc, 0, pid, 0, payload);
        }
        public void Dispose() { sustain.Dispose(); Resource.Dispose(); }
        private sealed class Lease(Broker owner, IKernelEtwSubscription actual) : IKernelEtwSubscription
        {
            private int released;
            public KernelEtwSessionSnapshot GetSnapshot() => actual.GetSnapshot();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                actual.Dispose(); owner.Releases++;
            }
        }
    }
}
