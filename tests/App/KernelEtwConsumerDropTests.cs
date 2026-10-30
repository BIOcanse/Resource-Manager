using System.Buffers.Binary;
using Microsoft.Diagnostics.Tracing.Parsers;
using Resource_Manager_APP.Tests.KernelEtwFixtures;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwConsumerDropTests
{
    [Fact]
    public void ConsumerCountersSurviveLateRegistrationAndRepeatedHolderAcquisitionWithoutGrowing()
    {
        using var resource = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration(qpcFrequency: 1000));
        using var flow = resource.Acquire(new("independent-flow-owner", KernelTraceEventParser.Keywords.Process,
            static (_, _) => { }) { NativeOutputs = KernelEtwNativeOutputs.Renderer });
        // The independent holder keeps physical intake alive while this dispatcher's bindings return.
        var dispatcher = new KernelEtwSubscriptionDispatcher(resource.Session, _ => { });
        var context = new KernelEtwSessionContext(resource.Generation, resource.StartedAt);
        KernelEtwEventSource? source = null;
        var registration = new KernelEtwSubscriptionRegistration(1, "finite-holder", KernelTraceEventParser.Keywords.Process,
            (bound, _) =>
            {
                source = bound;
                bound.ConsumerCounter("renderer-invalidation");
                bound.ConsumerRejectionCounter("renderer-invalidation", "invalid-process");
            }) { NativeOutputs = KernelEtwNativeOutputs.Renderer };
        dispatcher.InitializeSubscriptions([registration], context);
        try
        {
            var initial = source!.ConsumerCounter("renderer-invalidation");
            resource.RawProcess(1, 0, 0, "invalid.exe");
            Assert.Equal((1L, 1L), Counts(initial.Read()));
            var session = dispatcher.RegisterDiagnostics("consumer-counters-" + Guid.NewGuid().ToString("N"), () => 0);
            AssertHolder(1);
            for (var index = 0; index < 20; index++)
            {
                var previous = source;
                dispatcher.UpdateSubscriptions([], context, _ => throw new InvalidOperationException("Last return must not update keywords."));
                Assert.False(previous!.IsActive);
                dispatcher.UpdateSubscriptions([registration with { LeaseId = index + 2 }], context,
                    _ => throw new InvalidOperationException("The process flags remain enabled."));
                Assert.NotSame(previous, source);
                Assert.Same(initial, source!.ConsumerCounter("renderer-invalidation"));
                resource.RawProcess(index + 2, 0, 0, "invalid.exe");
                AssertHolder(index + 2);
            }
            AssertHolder(21);
            Assert.Equal(28, dispatcher.Counters.Length);
            Assert.All(dispatcher.Counters, counter => Assert.Equal(
                counter.Read().Kind == "ProcessStartGroup" ? (21L, 0L) : (0L, 0L), Counts(counter.Read())));

            void AssertHolder(long total)
            {
                var counts = session.Read().Kinds.Where(kind => kind.Kind.StartsWith("holder/finite-holder/", StringComparison.Ordinal)).ToArray();
                Assert.Equal(new[] { "holder/finite-holder/renderer-invalidation", "holder/finite-holder/renderer-invalidation/rejected:invalid-process" },
                    counts.Select(kind => kind.Kind).OrderBy(kind => kind, StringComparer.Ordinal).ToArray());
                Assert.All(counts, count => Assert.Equal((total, total), Counts(count)));
            }
        }
        finally { dispatcher.CancelAll(); }
    }

    [Fact]
    public void ReturnedResidencyBindingStopsAllFiveNativeKindsWhileIndependentIntakeStillPublishesData()
    {
        using var resource = new NativeKernelEtwTestResource(NativeKernelEtwTestResource.SmallConfiguration(qpcFrequency: 1000));
        var keywords = KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ContextSwitch;
        using var flow = resource.Acquire(new("independent-residency-owner", keywords, static (_, _) => { })
            { NativeOutputs = KernelEtwNativeOutputs.Residency });
        KernelEtwNativeReaders.ConfigureProcessors(resource.Session, [0]);
        resource.Publish(0);
        var dispatcher = new KernelEtwSubscriptionDispatcher(resource.Session, _ => { });
        var kinds = new[] { "ProcessStartGroup", "ProcessEndGroup", "ThreadStartGroup", "ThreadEndGroup", "ThreadCSwitch" };
        var context = new KernelEtwSessionContext(resource.Generation, resource.StartedAt);
        dispatcher.InitializeSubscriptions([new(1, "cpu-core-residency", keywords, (source, _) =>
        {
            foreach (var kind in kinds)
            {
                source.ConsumerCounter(kind);
                source.ConsumerRejectionCounter(kind, "generation-mismatch");
            }
        }) { NativeOutputs = KernelEtwNativeOutputs.Residency }], context);
        var session = dispatcher.RegisterDiagnostics("residency-return-" + Guid.NewGuid().ToString("N"), () => 0);
        try
        {
            DeliverFive(0, 41, 101);
            AssertHolder();
            AssertPublishedProcess(1010, 41);
            dispatcher.UpdateSubscriptions([], context, _ => throw new InvalidOperationException("Last return must not update keywords."));
            DeliverFive(1010, 42, 102);
            AssertHolder();
            AssertPublishedProcess(2020, 42);
            Assert.All(dispatcher.Counters, counter => Assert.Equal(
                counter.Read().Kind == "ThreadCSwitch" ? (4L, 0L)
                    : kinds.Contains(counter.Read().Kind, StringComparer.Ordinal) ? (2L, 0L) : (0L, 0L), Counts(counter.Read())));
        }
        finally { dispatcher.CancelAll(); }

        void AssertHolder()
        {
            var counts = session.Read().Kinds;
            foreach (var kind in kinds)
            {
                var prefix = "holder/cpu-core-residency/" + kind;
                Assert.Equal(kind == "ThreadCSwitch" ? (2L, 0L) : (1L, 0L),
                    Counts(Assert.Single(counts, value => value.Kind == prefix)));
                Assert.Equal((0L, 0L), Counts(Assert.Single(counts, value => value.Kind == prefix + "/rejected:generation-mismatch")));
            }
        }
        void DeliverFive(long offset, uint pid, uint tid)
        {
            resource.RawProcess(offset + 1, pid, 0, "residency.exe", pid);
            Span<byte> thread = stackalloc byte[68];
            thread.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(thread, pid);
            BinaryPrimitives.WriteUInt32LittleEndian(thread[4..], tid);
            var provider = new Guid("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
            resource.Raw(provider, 1, 2, 8, offset + 2, 0, pid, tid, thread);
            Span<byte> cswitch = stackalloc byte[24];
            cswitch.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(cswitch, tid);
            resource.Raw(provider, 36, 2, 8, offset + 3, 0, pid, tid, cswitch);
            // A residency row requires a full closed observation interval.
            // Supply its actual idle boundary before ending the lifetime.
            BinaryPrimitives.WriteUInt32LittleEndian(cswitch, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(cswitch[4..], tid);
            resource.Raw(provider, 36, 2, 8, offset + 1003, 0, pid, tid, cswitch);
            resource.Raw(provider, 2, 2, 8, offset + 1004, 0, pid, tid, thread);
            resource.RawProcess(offset + 1005, pid, 0, "residency.exe", pid, opcode: 2);
        }
        void AssertPublishedProcess(long tick, int pid)
        {
            var request = KernelEtwNativeReaders.RequestResidency(resource.Session);
            resource.Publish(tick);
            ulong sequence = 0;
            var payload = KernelEtwNativeReaders.ReadResidency(resource.Session, ref sequence, 0, request.Ticket, null,
                view => new CpuResidencyAggregationSnapshot(view.Frequency, view.StartQpc, view.ThroughQpc, null, (view.Flags & 1) != 0,
                    new KernelEtwNativeReaders.BorrowedResidencyRecords(view.Rows, view.Count).ToArray()));
            Assert.NotNull(payload);
            Assert.Contains(payload.Records, row => row.ProcessId == pid && row.ExecutionTimeQpc > 0);
        }
    }

    private static (long, long) Counts(EtwEventKindDrops value) => (value.Received, value.Dropped);
}
