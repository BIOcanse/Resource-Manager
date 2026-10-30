using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwSubscriptionDispatcherTests
{
    private static readonly KernelEtwSessionContext Context = new(17, DateTimeOffset.UnixEpoch);

    [Fact]
    public void RepeatedBindingsRetireNativeFramesAndKeepStableHolderTotalsBeyondTheDescriptorCap()
    {
        using var fixture = new NativeColdFixture();
        var dispatcher = new KernelEtwSubscriptionDispatcher(fixture.Session, _ => { });
        EtwEventDropCounter? counter = null;
        for (var index = 0; index < 130; index++) // Fixture declares 128 descriptors.
        {
            var registration = new KernelEtwSubscriptionRegistration(index + 1, "repeated-holder", KernelTraceEventParser.Keywords.Process,
                (source, _) => counter = source.ConsumerCounter("ProcessStartGroup")) { NativeOutputs = KernelEtwNativeOutputs.Residency };
            if (index == 0) dispatcher.InitializeSubscriptions([registration], fixture.Context);
            else dispatcher.UpdateSubscriptions([registration], fixture.Context, _ => throw new InvalidOperationException("Flags remain enabled."));
            fixture.ProcessStart(index + 1);
            dispatcher.UpdateSubscriptions([], fixture.Context, _ => throw new InvalidOperationException("No keyword call on last return."));
            Assert.NotNull(counter);
            Assert.Equal(index + 1, counter.Read().Received);
            Assert.Equal(0, counter.Read().Dropped);
        }
        dispatcher.CancelAll();
    }

    [Fact]
    public void CancelledConsumerIsSkippedByAPublicationThatCapturedTheOldSet()
    {
        using var fixture = new NativeColdFixture();
        var dispatcher = new KernelEtwSubscriptionDispatcher(fixture.Session, _ => { });
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var second = new Consumer(2, KernelTraceEventParser.Keywords.Process);
        first.OnDelivery = () => dispatcher.UpdateSubscriptions([first.Registration], Context,
            _ => throw new InvalidOperationException("Unchanged union must not be written."));
        dispatcher.InitializeSubscriptions([first.Registration, second.Registration], Context);
        fixture.Publish(1000); fixture.Publish(2000);
        Assert.Equal(2, first.Received); Assert.Equal(0, second.Received);
        Assert.False(second.Source!.IsActive); Assert.Equal(1, first.BindCount);
        Assert.All(first.Payloads, payload => Assert.Equal(fixture.Frequency, payload.QpcFrequency));
        Assert.Equal((2L, 0L), (dispatcher.PublicationCounter.Read().Received, dispatcher.PublicationCounter.Read().Dropped));
        dispatcher.CancelAll();
    }

    [Fact]
    public void CancelAllPreventsLaterPayloadDeliveryWithoutWaitingForTheCurrentCallback()
    {
        using var fixture = new NativeColdFixture();
        var dispatcher = new KernelEtwSubscriptionDispatcher(fixture.Session, _ => { });
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process) { OnDelivery = dispatcher.CancelAll };
        var second = new Consumer(2, KernelTraceEventParser.Keywords.Process);
        dispatcher.InitializeSubscriptions([first.Registration, second.Registration], Context);
        fixture.Publish(1000); fixture.Publish(2000);
        Assert.Equal(1, first.Received); Assert.Equal(0, second.Received);
        Assert.Single(first.Payloads); Assert.False(first.Source!.IsActive); Assert.False(second.Source!.IsActive);
        Assert.Equal((1L, 0L), (dispatcher.PublicationCounter.Read().Received, dispatcher.PublicationCounter.Read().Dropped));
    }

    [Fact]
    public void ABindingWithoutAPublicationObserverDoesNotConsumeThePublishedPayload()
    {
        using var fixture = new NativeColdFixture();
        var dispatcher = new KernelEtwSubscriptionDispatcher(fixture.Session, _ => { });
        var empty = new KernelEtwSubscriptionRegistration(1, "empty", KernelTraceEventParser.Keywords.Process, (_, _) => { })
            { NativeOutputs = KernelEtwNativeOutputs.Residency };
        dispatcher.InitializeSubscriptions([empty], Context); fixture.Publish(1000);
        var used = new Consumer(2, KernelTraceEventParser.Keywords.Process);
        dispatcher.UpdateSubscriptions([empty, used.Registration], Context, _ => { }); fixture.Publish(2000);
        dispatcher.UpdateSubscriptions([empty], Context, _ => { }); fixture.Publish(3000);
        Assert.Equal(1, used.Received); Assert.Single(used.Payloads);
        Assert.Equal((3L, 2L), (dispatcher.PublicationCounter.Read().Received, dispatcher.PublicationCounter.Read().Dropped));
        Assert.Equal(28, dispatcher.Counters.Length); dispatcher.CancelAll();
    }

    [Fact]
    public void DeniedRetriesKeepCoveredNativeBindingsAndLeavePendingKeywordsUnbound()
    {
        using var fixture = new NativeColdFixture();
        var dispatcher = new KernelEtwSubscriptionDispatcher(fixture.Session, _ => { });
        var first = new Consumer(1, KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.Process);
        var pending = new Consumer(2, KernelTraceEventParser.Keywords.DiskIO);
        var covered = new Consumer(3, KernelTraceEventParser.Keywords.Process);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var before = first.Source;
        for (var retry = 0; retry < 3; retry++)
        {
            Assert.Throws<UnauthorizedAccessException>(() => dispatcher.UpdateSubscriptions(
                [first.Registration, pending.Registration, covered.Registration], Context,
                _ => throw new UnauthorizedAccessException("fixture denied")));
            fixture.Publish((retry + 1) * 1000);
            Assert.Equal(retry + 1, first.Received); Assert.Equal(retry + 1, covered.Received);
            Assert.Equal(0, pending.Received);
            Assert.Equal(0, pending.BindCount); Assert.Null(pending.Source);
            Assert.Equal(1, first.BindCount); Assert.Equal(1, covered.BindCount);
            Assert.Same(before, first.Source);
            Assert.True(first.Source!.IsActive); Assert.True(covered.Source!.IsActive);
            Assert.Same(fixture.Session, first.Source.NativeSession);
        }
        dispatcher.UpdateSubscriptions([first.Registration, pending.Registration, covered.Registration], Context, _ => { });
        fixture.Publish(4000);
        Assert.Equal(4, first.Received); Assert.Equal(4, covered.Received); Assert.Equal(1, pending.Received);
        Assert.Equal(1, pending.BindCount); Assert.True(pending.Source!.IsActive);
        Assert.Equal(1, first.BindCount); Assert.Equal(1, covered.BindCount);
        dispatcher.CancelAll();
    }

    [Fact]
    public void DeniedReductionCancelsRemovedBindingAndPreservesItsSurvivor()
    {
        using var fixture = new NativeColdFixture();
        var dispatcher = new KernelEtwSubscriptionDispatcher(fixture.Session, _ => { });
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var removed = new Consumer(2, KernelTraceEventParser.Keywords.NetworkTCPIP);
        dispatcher.InitializeSubscriptions([first.Registration, removed.Registration], Context);
        var original = first.Source;
        Assert.Throws<UnauthorizedAccessException>(() => dispatcher.UpdateSubscriptions([first.Registration], Context,
            _ => throw new UnauthorizedAccessException("fixture denied")));
        fixture.Publish(1000);
        Assert.Equal(1, first.Received); Assert.Equal(0, removed.Received);
        Assert.True(first.Source!.IsActive); Assert.False(removed.Source!.IsActive);
        Assert.Same(original, first.Source); Assert.Equal(1, first.BindCount);
        var priorRemoved = removed.Source;
        dispatcher.UpdateSubscriptions([first.Registration, removed.Registration], Context,
            _ => throw new InvalidOperationException("Still-enabled flags cover this restoration."));
        fixture.Publish(2000);
        Assert.Equal(2, first.Received); Assert.Equal(1, removed.Received);
        Assert.NotSame(priorRemoved, removed.Source); Assert.Equal(2, removed.BindCount);
        Assert.True(removed.Source!.IsActive); Assert.False(priorRemoved!.IsActive);
        dispatcher.CancelAll();
    }

    [Fact]
    public void WithdrawingAllBindingsDoesNotWriteKeywordsAndDoesNotReviveCancelledSources()
    {
        using var fixture = new NativeColdFixture();
        var dispatcher = new KernelEtwSubscriptionDispatcher(fixture.Session, _ => { });
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var cancelled = first.Source;
        dispatcher.UpdateSubscriptions([], Context, _ => throw new InvalidOperationException("No control call expected."));
        fixture.Publish(1000); Assert.Equal(0, first.Received);
        Assert.False(cancelled!.IsActive);
        dispatcher.UpdateSubscriptions([first.Registration], Context, _ => throw new InvalidOperationException("Flags remain enabled."));
        fixture.Publish(2000); Assert.Equal(1, first.Received);
        Assert.NotSame(cancelled, first.Source); Assert.True(first.Source!.IsActive);
        Assert.False(cancelled.IsActive); Assert.Equal(2, first.BindCount);
        dispatcher.CancelAll();
    }

    [Fact]
    public void SameKeywordAdditionBindsImmediatelyWithoutRebindingTheExistingNativeReference()
    {
        using var fixture = new NativeColdFixture();
        var dispatcher = new KernelEtwSubscriptionDispatcher(fixture.Session, _ => { });
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var second = new Consumer(2, KernelTraceEventParser.Keywords.Process);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var activation = Context with { StartedAt = Context.StartedAt.AddMinutes(1) };
        dispatcher.UpdateSubscriptions([first.Registration, second.Registration], activation,
            _ => throw new InvalidOperationException("Unchanged union must not be written."));
        Assert.Equal(1, first.BindCount); Assert.Equal(1, second.BindCount);
        fixture.Publish(1000); Assert.Equal(1, first.Received); Assert.Equal(1, second.Received);
        Assert.Equal(Context, first.Context); Assert.Equal(activation, second.Context);
        Assert.True(first.Source!.IsActive); Assert.True(second.Source!.IsActive);
        Assert.Same(first.Source.NativeSession, second.Source.NativeSession);
        dispatcher.CancelAll();
    }

    [Fact]
    public void NewNativeBindingIsNotPublishedBeforeKeywordUpdateSucceeds()
    {
        using var fixture = new NativeColdFixture();
        var dispatcher = new KernelEtwSubscriptionDispatcher(fixture.Session, _ => { });
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var second = new Consumer(2, KernelTraceEventParser.Keywords.NetworkTCPIP);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var updates = new List<KernelTraceEventParser.Keywords>();
        dispatcher.UpdateSubscriptions([first.Registration, second.Registration], Context, flags =>
        { updates.Add(flags); fixture.Publish(1000); Assert.Equal(1, first.Received); Assert.Equal(0, second.Received);
            Assert.Null(second.Source); Assert.Equal(0, second.BindCount); Assert.True(first.Source!.IsActive); });
        fixture.Publish(2000);
        Assert.Equal(2, first.Received); Assert.Equal(1, second.Received); Assert.Single(second.Payloads);
        Assert.Equal(new[] { KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.NetworkTCPIP }, updates);
        Assert.True(second.Source!.IsActive); Assert.Equal(1, first.BindCount);
        dispatcher.CancelAll();
    }

    [Fact]
    public void BindingFailureAfterUpdateKeepsThePreviousNativeSetPublished()
    {
        using var fixture = new NativeColdFixture();
        var dispatcher = new KernelEtwSubscriptionDispatcher(fixture.Session, _ => { });
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var original = first.Source;
        var failure = new InvalidOperationException("fixture binding failure");
        var invalid = new KernelEtwSubscriptionRegistration(2, "invalid", KernelTraceEventParser.Keywords.NetworkTCPIP, (_, _) => throw failure);
        var updates = new List<KernelTraceEventParser.Keywords>();
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => dispatcher.UpdateSubscriptions([first.Registration, invalid], Context, updates.Add)));
        fixture.Publish(1000); Assert.Equal(1, first.Received);
        Assert.Same(original, first.Source); Assert.True(original!.IsActive); Assert.Equal(1, first.BindCount);
        Assert.Equal(new[] { KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.NetworkTCPIP }, updates);
        dispatcher.CancelAll();
    }

    [Fact]
    public void NativeUpdateFailurePreservesItsErrorAndDoesNotActivateANewBinding()
    {
        using var fixture = new NativeColdFixture();
        var dispatcher = new KernelEtwSubscriptionDispatcher(fixture.Session, _ => { });
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var pending = new Consumer(2, KernelTraceEventParser.Keywords.NetworkTCPIP);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var failure = new InvalidOperationException("fixture keyword failure");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => dispatcher.UpdateSubscriptions(
            [first.Registration, pending.Registration], Context, _ => throw failure)));
        fixture.Publish(1000); Assert.Equal(1, first.Received); Assert.Equal(0, pending.Received);
        Assert.True(first.Source!.IsActive); Assert.Null(pending.Source); Assert.Equal(0, pending.BindCount);
        dispatcher.CancelAll();
        Assert.False(first.Source.IsActive);
    }

    [Fact]
    public void RequestedOutputsArePreparedBeforeTheKeywordChangeThatEmitsTheirRundown()
    {
        using var fixture = new NativeColdFixture();
        var applied = new List<KernelEtwNativeOutputs>();
        var dispatcher = new KernelEtwSubscriptionDispatcher(fixture.Session, applied.Add);
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var binds = 0;
        // Enabling ImageLoad emits a one-time rundown, kernel modules first. The
        // interrupt output must exist before that change; its binding still waits.
        var interrupts = new KernelEtwSubscriptionRegistration(2, "interrupts", KernelTraceEventParser.Keywords.ImageLoad,
            (_, _) => binds++) { NativeOutputs = KernelEtwNativeOutputs.Interrupts };
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var atUpdate = new List<(KernelEtwNativeOutputs Outputs, int Binds)>();
        Assert.Throws<UnauthorizedAccessException>(() => dispatcher.UpdateSubscriptions([first.Registration, interrupts], Context, _ =>
        { atUpdate.Add((applied[^1], binds)); throw new UnauthorizedAccessException("fixture denied"); }));
        // A denied change returns the prepared output with the covered set.
        Assert.Equal(new[] { KernelEtwNativeOutputs.Residency, KernelEtwNativeOutputs.Residency | KernelEtwNativeOutputs.Interrupts,
            KernelEtwNativeOutputs.Residency }, applied);
        dispatcher.UpdateSubscriptions([first.Registration, interrupts], Context, _ => atUpdate.Add((applied[^1], binds)));
        var prepared = (KernelEtwNativeOutputs.Residency | KernelEtwNativeOutputs.Interrupts, 0);
        Assert.Equal(new[] { prepared, prepared }, atUpdate);
        Assert.Equal(1, binds); Assert.Equal(KernelEtwNativeOutputs.Residency | KernelEtwNativeOutputs.Interrupts, applied[^1]);
        dispatcher.CancelAll();
    }

    private sealed class Consumer(long id, KernelTraceEventParser.Keywords keywords)
    {
        internal int BindCount, Received;
        internal Action? OnDelivery;
        internal readonly List<CpuResidencyAggregationSnapshot> Payloads = [];
        private ulong sequence;
        internal KernelEtwEventSource? Source;
        internal KernelEtwSessionContext? Context;
        internal KernelEtwSubscriptionRegistration Registration => new(id, $"consumer-{id}", keywords,
            (source, context) =>
            {
                BindCount++; Source = source; Context = context;
                source.ObservePublications(() =>
                {
                    var payload = KernelEtwNativeReaders.ReadResidency(source.NativeSession, ref sequence, 0, 0, null,
                        view => new(view.Frequency, view.StartQpc, view.ThroughQpc, null, (view.Flags & 1) != 0,
                            new KernelEtwNativeReaders.BorrowedResidencyRecords(view.Rows, view.Count).ToArray()));
                    Assert.NotNull(payload); Payloads.Add(payload); Received++; OnDelivery?.Invoke();
                });
            }) { NativeOutputs = KernelEtwNativeOutputs.Residency };
    }
    private sealed class NativeColdFixture : IDisposable
    {
        private readonly NativeKernelEtwTestResource resource = new(NativeKernelEtwTestResource.SmallConfiguration());
        private readonly IKernelEtwSubscription hold;
        internal NativeKernelEtwSession Session => resource.Session;
        internal long Frequency => resource.QpcFrequency;
        internal KernelEtwSessionContext Context => new(resource.Generation, resource.StartedAt);
        // Counters describe real parser intake. The normalized CPU-state helper
        // intentionally bypasses that decoder/admission path.
        internal void ProcessStart(long qpc) => resource.RawProcess(qpc, 42, 0, "fixture-process", 1);
        internal void Publish(long tick) => resource.Publish(tick);
        internal NativeColdFixture() => hold = resource.Acquire(new("fixture-resource", KernelTraceEventParser.Keywords.Process,
            static (_, _) => { }) { NativeOutputs = KernelEtwNativeOutputs.Residency });
        public void Dispose() { hold.Dispose(); resource.Dispose(); }
    }
}
