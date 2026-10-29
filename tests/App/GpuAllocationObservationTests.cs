using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationObservationTests
{
    [Fact]
    public void ExactSessionScopeRecordsNativeCommandBoundariesAndFailedReturns()
    {
        var sink = new Sink();
        var provider = GpuAllocationCollector.Provider;
        using var scope = GpuAllocationObservation.Subscribe("owned-gpu-observation", sink);
        var fail = false;
        var control = new GpuAllocationProviderControl(EtwEventDropDiagnostics.Start("observed-provider", () => 0),
            (_, _, _) => { if (fail) throw new IOException("enable failed"); },
            _ => { }, (_, _, _, _) => { }, "owned-gpu-observation");
        control.Enable(provider, 0x41, [35]);
        control.CaptureState(provider, 0x41, [35]);
        control.Disable(provider);
        fail = true;
        Assert.Throws<IOException>(() => control.Enable(provider, 0x41, [35]));
        Assert.Equal(["enable", "enable", "capture-state", "capture-state", "disable", "disable", "enable", "enable"],
            sink.Commands.Select(value => value.Operation));
        Assert.All(sink.Commands.Where(value => !value.Completed), value => Assert.Null(value.Succeeded));
        Assert.All(sink.Commands.Take(6).Where(value => value.Completed), value => Assert.True(value.Succeeded));
        Assert.False(sink.Commands[^1].Succeeded);
        Assert.All(sink.Commands, value => Assert.Equal(provider, value.Provider));
        Assert.Equal(sink.Commands.OrderBy(value => value.At), sink.Commands);
        GpuAllocationObservation.RecordCommand("another-session", "enable", provider, false, null);
        Assert.Equal(8, sink.Commands.Count);
    }

    [Fact]
    public void DeliveryKeepsEtwTimeSeparateFromReceiptAndCountsConsumptionWithoutAnotherReceive()
    {
        var sink = new Sink();
        var timestamp = DateTime.UtcNow.AddSeconds(-5);
        using (GpuAllocationObservation.Subscribe("owned-gpu-events", sink))
        {
            GpuAllocationObservation.RecordDelivery("another-session", GpuAllocationCollector.Provider, 35, timestamp, false);
            GpuAllocationObservation.RecordDelivery("owned-gpu-events", GpuAllocationCollector.Provider, 35, timestamp, true);
            GpuAllocationObservation.RecordDelivery("owned-gpu-events", GpuAllocationCollector.Provider, 18, timestamp, false);
        }
        GpuAllocationObservation.RecordDelivery("owned-gpu-events", GpuAllocationCollector.Provider, 35, timestamp, true);
        Assert.Equal(2, sink.Deliveries.Count);
        Assert.True(sink.Deliveries[0].Accepted);
        Assert.False(sink.Deliveries[1].Accepted);
        Assert.All(sink.Deliveries, value =>
        {
            Assert.Equal(timestamp, value.EventTimestampUtc);
            Assert.True(value.ReceivedAt.UtcDateTime > value.EventTimestampUtc);
        });
    }

    [Fact]
    public void ObservationCannotAlterNativeOwnershipAndOnlyOneScopedSinkCanExist()
    {
        using (GpuAllocationObservation.Subscribe("owned-gpu-throw", new ThrowingSink()))
        {
            Assert.Throws<InvalidOperationException>(() => GpuAllocationObservation.Subscribe("duplicate", new Sink()));
            GpuAllocationObservation.RecordCommand("owned-gpu-throw", "enable", Guid.Empty, true, true);
            GpuAllocationObservation.RecordDelivery("owned-gpu-throw", Guid.Empty, 1, DateTime.UtcNow, false);
        }
        using var replacement = GpuAllocationObservation.Subscribe("replacement", new Sink());
    }

    [Fact]
    public void NormalUnobservedDeliveryAndCommandsAllocateNoDiagnosticObjects()
    {
        var provider = GpuAllocationCollector.Provider;
        var timestamp = DateTime.UtcNow;
        // Warm the no-subscriber paths before measuring current-thread bytes.
        GpuAllocationObservation.RecordCommand("unobserved", "enable", provider, false, null);
        GpuAllocationObservation.RecordDelivery("unobserved", provider, 35, timestamp, true);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            GpuAllocationObservation.RecordCommand("unobserved", "enable", provider, false, null);
            GpuAllocationObservation.RecordDelivery("unobserved", provider, 35, timestamp, true);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class Sink : GpuAllocationObservation.ISink
    {
        internal readonly List<GpuAllocationObservation.Command> Commands = [];
        internal readonly List<GpuAllocationObservation.Delivery> Deliveries = [];
        public void OnCommand(GpuAllocationObservation.Command value) => Commands.Add(value);
        public void OnDelivery(GpuAllocationObservation.Delivery value) => Deliveries.Add(value);
    }
    private sealed class ThrowingSink : GpuAllocationObservation.ISink
    {
        public void OnCommand(GpuAllocationObservation.Command value) => throw new IOException("diagnostic failed");
        public void OnDelivery(GpuAllocationObservation.Delivery value) => throw new IOException("diagnostic failed");
    }
}
