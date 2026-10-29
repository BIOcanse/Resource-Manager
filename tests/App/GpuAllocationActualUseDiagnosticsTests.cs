using System.Buffers.Binary;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationActualUseDiagnosticsTests
{
    private static readonly int[] DecoderIds = [33, 34, 35, 36, 37, 38, 39, 40, 53, 54, 55,
        74, 78, 80, 110, 227, 306, 307, 313, 314, 391];

    [Fact]
    public void NativeCaptureRetainsTheExactEnableFilterAndRecordsOnlySuccessfulProviderChanges()
    {
        var name = "gpu-provider-controls-" + Guid.NewGuid();
        var drops = EtwEventDropDiagnostics.Start(name, () => 0);
        TraceEventProviderOptions? enabledOptions = null;
        var filterType = 0;
        byte[]? captureFilter = null;
        var failEnable = false;
        var failDisable = false;
        var commands = new List<string>();
        var provider = GpuAllocationCollector.Provider;
        var control = new GpuAllocationProviderControl(drops,
            (id, keywords, options) =>
            {
                Assert.Equal(provider, id);
                Assert.Equal(0x41UL, keywords);
                if (failEnable) throw new IOException("enable");
                enabledOptions = options;
                commands.Add("enable");
            },
            id =>
            {
                Assert.Equal(provider, id);
                if (failDisable) throw new IOException("disable");
                commands.Add("disable");
            },
            (id, keywords, type, data) =>
            {
                Assert.Equal(provider, id);
                Assert.Equal(0x41UL, keywords);
                filterType = type;
                captureFilter = data;
                commands.Add("capture");
            });
        EtwSessionEventDrops Current() => Assert.Single(EtwEventDropDiagnostics.Read().Sessions, item => item.Name == name);

        Assert.Empty(Current().EnabledProviders);
        failEnable = true;
        Assert.Throws<IOException>(() => control.Enable(provider, 0x41, DecoderIds));
        Assert.Empty(Current().EnabledProviders);
        failEnable = false;
        control.Enable(provider, 0x41, DecoderIds);
        var enabled = Assert.Single(Current().EnabledProviders);
        Assert.Equal(provider.ToString("D"), enabled.Provider);
        Assert.Equal(0x41UL, enabled.Keywords);
        Assert.Equal(DecoderIds, enabledOptions!.EventIDsToEnable);

        control.CaptureState(provider, 0x41, DecoderIds);
        Assert.Equal(unchecked((int)0x80000200), filterType);
        Assert.NotNull(captureFilter);
        Assert.Equal(4 + DecoderIds.Length * 2, captureFilter.Length);
        Assert.Equal(1, captureFilter[0]);
        Assert.Equal(0, captureFilter[1]);
        Assert.Equal(DecoderIds.Length, BinaryPrimitives.ReadUInt16LittleEndian(captureFilter.AsSpan(2)));
        Assert.Equal(DecoderIds, Enumerable.Range(0, DecoderIds.Length)
            .Select(index => (int)BinaryPrimitives.ReadUInt16LittleEndian(captureFilter.AsSpan(4 + 2 * index))));
        Assert.Equal(["enable", "capture"], commands);

        failDisable = true;
        Assert.Throws<IOException>(() => control.Disable(provider));
        Assert.Single(Current().EnabledProviders);
        failDisable = false;
        control.Disable(provider);
        Assert.Empty(Current().EnabledProviders);
        control.Enable(provider, 0x41, DecoderIds);
        control.RecordSessionReleased();
        Assert.Empty(Current().EnabledProviders);
        drops.Complete();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public async Task FiniteAndLiveRundownPassTheSameExactReadIdsToEnableAndCapture(int cadenceSeconds)
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        Assert.Empty(factory.Sessions);
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        Assert.Empty(factory.Sessions);
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(cadenceSeconds), default);
        var session = factory.Current;
        var enables = session.EnableHistory.Where(item => item.Provider == GpuAllocationCollector.Provider).ToArray();
        Assert.Equal(2, enables.Length);
        var enable = enables[0];
        var capture = Assert.Single(session.CaptureHistory);
        Assert.Equal(0x41UL, enable.Keywords);
        Assert.Equal(enable.Keywords, capture.Keywords);
        Assert.Equal(DecoderIds, enable.EventIds);
        Assert.Equal(DecoderIds, capture.EventIds);
        Assert.Equal(enable.Keywords, enables[1].Keywords);
        Assert.Equal(DecoderIds, enables[1].EventIds);
        Assert.NotNull(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(cadenceSeconds == 1, session.Enabled.Contains(GpuAllocationCollector.Provider));
        await harness.StopAsync();
        Assert.Empty(session.Enabled);
        Assert.True(session.Joined);
    }

    [Fact]
    public async Task CollectorRejectionsUseTheDeliveredKindsCounterIncludingLateCallbacks()
    {
        var name = "gpu-collector-rejections-" + Guid.NewGuid();
        var drops = EtwEventDropDiagnostics.Start(name, () => 0);
        var factory = new GpuAllocationTestSessionFactory { Configure = session => session.HoldEnd = true };
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        var capture = harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        var session = factory.Current;
        await session.FirstEndScanQueued;
        var callback = session.BoundHandler;
        void Deliver(Guid provider, int id, params (string Field, object Value)[] fields)
        {
            var counter = drops.Kind(provider, id, 0);
            counter.Receive();
            var values = fields.ToDictionary(item => item.Field, item => item.Value);
            callback(provider, id, new GpuAllocationNamedPayload(key => values[key], counter), DateTime.UtcNow);
        }
        var allocation = GpuAllocationCollector.Provider;
        var boundary = EventSource.GetGuid(typeof(GpuAllocationBoundary));
        Deliver(allocation, 18); // Outside the decoder's 21 IDs.
        Deliver(Guid.Empty, 18);
        Deliver(boundary, 65534); // EventSource schema has no boundary fields.
        Deliver(boundary, 2, ("session", "another-session"));
        Deliver(boundary, 3, ("session", session.Name), ("scan", -1L));
        Deliver(allocation, 80, ("hAllocationHandle", 11UL), ("hProcessId", 41U),
            ("ulSegmentId", 2U), ("Size", 2048UL), ("SegmentOffset", 0UL));
        Deliver(allocation, 35); // Decoder failure invalidates this candidate.
        Deliver(allocation, 33); // No candidate or live ledger now consumes it.
        Deliver(boundary, 3, ("session", session.Name), ("scan", 1L));
        await capture;
        Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.True(session.Joined);
        Deliver(allocation, 33); // Already-released session's in-flight delivery.

        var snapshot = Assert.Single(EtwEventDropDiagnostics.Read().Sessions, item => item.Name == name);
        Assert.Equal(10, snapshot.Kinds.Sum(item => item.Received));
        Assert.Equal(9, snapshot.Kinds.Sum(item => item.Dropped));
        var accepted = Assert.Single(snapshot.Kinds, item => item.Kind == $"{allocation:D}/80/0");
        Assert.Equal(1, accepted.Received);
        Assert.Equal(0, accepted.Dropped);
        Assert.All(snapshot.Kinds.Where(item => item != accepted), item => Assert.Equal(item.Received, item.Dropped));
        drops.Complete();
    }
}
