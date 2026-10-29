using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwConsumerDropTests
{
    [Fact]
    public void ConsumerCountersSurviveLateRegistrationAndRepeatedHolderAcquisitionWithoutGrowing()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var context = new KernelEtwSessionContext(19, DateTimeOffset.UnixEpoch);
        KernelEtwEventSource? source = null;
        var registration = new KernelEtwSubscriptionRegistration(1, "finite-holder",
            KernelTraceEventParser.Keywords.Process, (bound, _) => source = bound);
        dispatcher.InitializeSubscriptions([registration], context);
        var initial = source!.ConsumerCounter("ProcessStartGroup");
        initial.Receive(); initial.Drop();
        source.RecordConsumerRejection("ProcessStartGroup", "stale");
        var session = dispatcher.RegisterDiagnostics("consumer-counters-" + Guid.NewGuid().ToString("N"), () => 0);
        for (var index = 0; index < 20; index++)
        {
            dispatcher.UpdateSubscriptions([], context, _ => { });
            dispatcher.UpdateSubscriptions([registration with { LeaseId = index + 2 }], context, _ => { });
            var next = source!.ConsumerCounter("ProcessStartGroup");
            Assert.Same(initial, next);
            next.Receive(); next.Drop();
            source.RecordConsumerRejection("ProcessStartGroup", "stale");
        }
        var counts = session.Read().Kinds.Where(kind => kind.Kind.StartsWith("holder/finite-holder/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, counts.Length);
        Assert.All(counts, count => Assert.Equal((21L, 21L), (count.Received, count.Dropped)));
        Assert.All(dispatcher.Counters, counter => Assert.Equal((0L, 0L), (counter.Read().Received, counter.Read().Dropped)));
    }

    [Fact]
    public void StaleResidencyMetadataAndSwitchCallbacksRecordEachRejectionWithoutReadingPayload()
    {
        var session = EtwEventDropDiagnostics.Start("residency-rejections-" + Guid.NewGuid().ToString("N"), () => 0);
        var source = new KernelEtwEventSource("cpu-core-residency", session.NamedKind);
        var buffer = new CpuResidencyAggregationBuffer(1000, 50, 16);
        EtwCpuResidencyCaptureOwner.BindKernelEventCallbacks(source, buffer, () => false, static _ => null);
        source.DeliverProcessStartGroup(null!);
        source.DeliverProcessEndGroup(null!);
        source.DeliverThreadStartGroup(null!);
        source.DeliverThreadEndGroup(null!);
        source.DeliverThreadCSwitch(null!);
        var kinds = session.Read().Kinds;
        foreach (var kind in new[] { "ProcessStartGroup", "ProcessEndGroup", "ThreadStartGroup", "ThreadEndGroup", "ThreadCSwitch" })
        {
            var prefix = "holder/cpu-core-residency/" + kind;
            Assert.Equal((1L, 1L), Read(prefix));
            Assert.Equal((1L, 1L), Read(prefix + "/rejected:generation-mismatch"));
        }
        buffer.Clear();
        (long, long) Read(string name)
        {
            var value = Assert.Single(kinds.Where(value => value.Kind == name));
            return (value.Received, value.Dropped);
        }
    }
}
