using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwSubscriptionDispatcherTests
{
    private static readonly KernelEtwSessionContext Context = new(17, DateTimeOffset.UnixEpoch);

    [Fact]
    public void SameKeywordSubscriptionActivatesWithoutEventsOrRebindingExistingConsumers()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var second = new Consumer(2, KernelTraceEventParser.Keywords.Process);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var updates = new List<KernelTraceEventParser.Keywords>();
        var activation = Context with { StartedAt = Context.StartedAt.AddMinutes(1) };

        dispatcher.UpdateSubscriptions([first.Registration, second.Registration], activation, updates.Add);
        EmitProcessStart(dispatcher);

        Assert.Equal(1, first.BindCount);
        Assert.Equal(1, second.BindCount);
        Assert.Equal(Context, first.Context);
        Assert.Equal(activation, second.Context);
        Assert.Equal(1, first.Received);
        Assert.Equal(1, second.Received);
        Assert.Empty(updates);
    }

    [Fact]
    public void NewConsumerIsUnpublishedUntilTheKeywordUpdateSucceeds()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var second = new Consumer(2, KernelTraceEventParser.Keywords.NetworkTCPIP);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var updates = new List<KernelTraceEventParser.Keywords>();

        dispatcher.UpdateSubscriptions([first.Registration, second.Registration], Context, keywords =>
        {
            updates.Add(keywords);
            EmitProcessStart(dispatcher);
            Assert.Equal(0, second.Received);
            Assert.False(second.Source!.IsActive);
        });
        EmitProcessStart(dispatcher);

        Assert.Equal(new[] { KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.NetworkTCPIP }, updates);
        Assert.Equal(2, first.Received);
        Assert.Equal(1, second.Received);
        Assert.True(second.Source!.IsActive);
    }

    [Fact]
    public void CancelledConsumerIsSkippedEvenByADispatchThatAlreadyCapturedTheOldSet()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var second = new Consumer(2, KernelTraceEventParser.Keywords.Process);
        first.OnDelivery = () => dispatcher.UpdateSubscriptions([first.Registration], Context,
            _ => throw new InvalidOperationException("Unchanged keywords must not be written."));
        dispatcher.InitializeSubscriptions([first.Registration, second.Registration], Context);

        EmitProcessStart(dispatcher);
        EmitProcessStart(dispatcher);

        Assert.Equal(2, first.Received);
        Assert.Equal(0, second.Received);
        Assert.False(second.Source!.IsActive);
        Assert.Equal(1, first.BindCount);
    }

    [Fact]
    public void RemovingAUniqueKeywordReducesTheNativeUnionWithoutRebindingTheSurvivor()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var second = new Consumer(2, KernelTraceEventParser.Keywords.NetworkTCPIP);
        dispatcher.InitializeSubscriptions([first.Registration, second.Registration], Context);
        var updates = new List<KernelTraceEventParser.Keywords>();

        dispatcher.UpdateSubscriptions([first.Registration], Context, updates.Add);
        EmitProcessStart(dispatcher);

        Assert.Equal(new[] { KernelTraceEventParser.Keywords.Process }, updates);
        Assert.Equal(1, first.BindCount);
        Assert.Equal(1, first.Received);
        Assert.Equal(0, second.Received);
    }

    [Fact]
    public void FailedBindingDoesNotChangeThePublishedSetOrWriteNativeKeywords()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var failure = new InvalidOperationException("fixture binding failure");
        var invalid = new KernelEtwSubscriptionRegistration(2, "invalid",
            KernelTraceEventParser.Keywords.NetworkTCPIP, (_, _) => throw failure);

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => dispatcher.UpdateSubscriptions(
            [first.Registration, invalid], Context,
            _ => throw new InvalidOperationException("A failed binding must not write native keywords."))));
        EmitProcessStart(dispatcher);

        Assert.Equal(1, first.Received);
        Assert.True(first.Source!.IsActive);
    }

    [Fact]
    public void FailedNativeUpdateDoesNotActivateNewCallbacksAndPreservesTheFailure()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var second = new Consumer(2, KernelTraceEventParser.Keywords.NetworkTCPIP);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var failure = new InvalidOperationException("fixture keyword failure");

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => dispatcher.UpdateSubscriptions(
            [first.Registration, second.Registration], Context, _ => throw failure)));
        EmitProcessStart(dispatcher);

        Assert.Equal(1, first.Received);
        Assert.Equal(0, second.Received);
        Assert.False(second.Source!.IsActive);
    }

    [Fact]
    public void CancelAllPreventsFurtherDeliveryWithoutWaitingForTheCurrentCallback()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var second = new Consumer(2, KernelTraceEventParser.Keywords.Process);
        first.OnDelivery = dispatcher.CancelAll;
        dispatcher.InitializeSubscriptions([first.Registration, second.Registration], Context);

        EmitProcessStart(dispatcher);
        EmitProcessStart(dispatcher);

        Assert.Equal(1, first.Received);
        Assert.Equal(0, second.Received);
        Assert.False(first.Source!.IsActive);
        Assert.False(second.Source!.IsActive);
    }

    // These fixtures test synchronous dispatch admission, not ETL payload decoding.
    // The existing ETL replay tests cover parser-to-production-reader delivery.
    private static void EmitProcessStart(KernelEtwSubscriptionDispatcher dispatcher)
        => dispatcher.Dispatch<ProcessTraceData>(null!, static (source, data) => source.DeliverProcessStartGroup(data));

    private sealed class Consumer(long leaseId, KernelTraceEventParser.Keywords keywords)
    {
        public int BindCount { get; private set; }
        public int Received { get; private set; }
        public KernelEtwSessionContext? Context { get; private set; }
        public KernelEtwEventSource? Source { get; private set; }
        public Action? OnDelivery { get; set; }
        public KernelEtwSubscriptionRegistration Registration => new(leaseId, $"consumer-{leaseId}", keywords, Bind);

        private void Bind(KernelEtwEventSource source, KernelEtwSessionContext context)
        {
            BindCount++;
            Source = source;
            Context = context;
            source.ProcessStartGroup += _ =>
            {
                OnDelivery?.Invoke();
                Received++;
            };
        }
    }
}
