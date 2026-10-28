using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwSubscriptionDispatcherTests
{
    private static readonly KernelEtwSessionContext Context = new(17, DateTimeOffset.UnixEpoch);

    public static IEnumerable<object[]> EventKinds => Enum.GetNames<KernelEtwEventKind>().Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(EventKinds))]
    public void EveryKindDistinguishesAnEmptyBindingFromARealHandler(string name)
    {
        var payload = typeof(KernelEtwEventSource).GetEvent(name)!.EventHandlerType!.GetGenericArguments()[0];
        typeof(KernelEtwSubscriptionDispatcherTests).GetMethod(nameof(CheckKind),
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericMethod(payload).Invoke(null, [name]);
    }

    private static void CheckKind<T>(string name)
    {
        var kind = Enum.Parse<KernelEtwEventKind>(name);
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var delivered = 0;
        var deliverMethod = typeof(KernelEtwEventSource).GetMethod("Deliver" + name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        bool Deliver(KernelEtwEventSource source, T data) => (bool)deliverMethod.Invoke(source, [data])!;
        var empty = new KernelEtwSubscriptionRegistration(1, "empty", KernelTraceEventParser.Keywords.Process, (_, _) => { });
        var used = new KernelEtwSubscriptionRegistration(2, "used", KernelTraceEventParser.Keywords.Process,
            (source, _) => typeof(KernelEtwEventSource).GetEvent(name)!.AddEventHandler(source, (Action<T>)(_ => delivered++)));
        dispatcher.InitializeSubscriptions([empty], Context);
        dispatcher.Dispatch<T>(kind, default!, Deliver);
        dispatcher.UpdateSubscriptions([empty, used], Context, _ => { });
        dispatcher.Dispatch<T>(kind, default!, Deliver);
        dispatcher.UpdateSubscriptions([empty], Context, _ => { });
        dispatcher.Dispatch<T>(kind, default!, Deliver);
        var count = dispatcher.Counters[(int)kind].Read();
        Assert.Equal(name, count.Kind);
        Assert.Equal((3L, 2L), (count.Received, count.Dropped));
        Assert.Equal(1, delivered);
    }

    [Fact]
    public void CountsEachReceivedKindOnceAndDropsOnlyWhenEveryActiveBindingLacksAHandler()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var consumer = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var empty = new KernelEtwSubscriptionRegistration(2, "empty", KernelTraceEventParser.Keywords.Process, (_, _) => { });
        dispatcher.InitializeSubscriptions([consumer.Registration, empty], Context);
        EmitProcessStart(dispatcher);
        dispatcher.Dispatch<ProcessTraceData>(KernelEtwEventKind.ProcessEndGroup, null!,
            static (source, data) => source.DeliverProcessEndGroup(data));
        dispatcher.UpdateSubscriptions([empty], Context, _ => { });
        EmitProcessStart(dispatcher);
        dispatcher.CancelAll();
        EmitProcessStart(dispatcher);

        var start = dispatcher.Counters[(int)KernelEtwEventKind.ProcessStartGroup].Read();
        var end = dispatcher.Counters[(int)KernelEtwEventKind.ProcessEndGroup].Read();
        Assert.Equal((3L, 2L), (start.Received, start.Dropped));
        Assert.Equal((1L, 1L), (end.Received, end.Dropped));
        Assert.Equal(1, consumer.Received);
        Assert.Equal(28, dispatcher.Counters.Length);
        var next = new KernelEtwSubscriptionDispatcher();
        Assert.All(next.Counters, counter => Assert.Equal((0L, 0L), (counter.Read().Received, counter.Read().Dropped)));
    }

    [Fact]
    public void CancellingLaterBindingsDuringDeliveryDoesNotDropAnAlreadyUsedEvent()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var consumer = new Consumer(1, KernelTraceEventParser.Keywords.Process) { OnDelivery = dispatcher.CancelAll };
        dispatcher.InitializeSubscriptions([consumer.Registration], Context);
        EmitProcessStart(dispatcher);
        EmitProcessStart(dispatcher);
        var count = dispatcher.Counters[(int)KernelEtwEventKind.ProcessStartGroup].Read();
        Assert.Equal((2L, 1L), (count.Received, count.Dropped));
    }

    [Fact]
    public void SteadyStateDispatchDoesNotAllocateWithOrWithoutAHandler()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var consumer = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        dispatcher.InitializeSubscriptions([consumer.Registration], Context);
        EmitProcessStart(dispatcher); // Warm the cached delivery delegate.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) EmitProcessStart(dispatcher);
        var usedAllocations = GC.GetAllocatedBytesForCurrentThread() - before;
        dispatcher.CancelAll();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) EmitProcessStart(dispatcher);
        var droppedAllocations = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, usedAllocations);
        Assert.Equal(0, droppedAllocations);
        var count = dispatcher.Counters[(int)KernelEtwEventKind.ProcessStartGroup].Read();
        Assert.Equal((2001L, 1000L), (count.Received, count.Dropped));
    }

    [Fact]
    public void DeniedRetriesLeaveUncoveredConsumersUnboundUntilKeywordsAreEnabled()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var first = new Consumer(1, KernelTraceEventParser.Keywords.ContextSwitch);
        var pending = new Consumer(2, KernelTraceEventParser.Keywords.DiskIO);
        var covered = new Consumer(3, KernelTraceEventParser.Keywords.Process);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var before = first.Source;
        for (var retry = 0; retry < 3; retry++)
        {
            Assert.Throws<UnauthorizedAccessException>(() => dispatcher.UpdateSubscriptions(
                [first.Registration, pending.Registration, covered.Registration], Context,
                _ => throw new UnauthorizedAccessException("fixture denied")));
            EmitProcessStart(dispatcher);
            Assert.Equal(0, pending.BindCount);
            Assert.Null(pending.Source);
            Assert.Equal(1, first.BindCount);
            Assert.Equal(1, covered.BindCount);
            Assert.Same(before, first.Source);
            Assert.Equal(retry + 1, first.Received);
            Assert.Equal(retry + 1, covered.Received);
        }

        dispatcher.UpdateSubscriptions([first.Registration, pending.Registration, covered.Registration], Context, _ => { });
        EmitProcessStart(dispatcher);
        Assert.Equal(1, pending.BindCount);
        Assert.Equal(1, pending.Received);
        Assert.Equal(1, first.BindCount);
        Assert.Equal(1, covered.BindCount);
    }

    [Fact]
    public void DeniedReductionRemovesWithdrawnCallbacksAndPreservesTheSurvivor()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        var removed = new Consumer(2, KernelTraceEventParser.Keywords.NetworkTCPIP);
        dispatcher.InitializeSubscriptions([first.Registration, removed.Registration], Context);
        Assert.Throws<UnauthorizedAccessException>(() => dispatcher.UpdateSubscriptions(
            [first.Registration], Context, _ => throw new UnauthorizedAccessException("fixture denied")));
        EmitProcessStart(dispatcher);
        Assert.Equal(1, first.Received);
        Assert.Equal(0, removed.Received);
        Assert.Equal(1, first.BindCount);

        // Restoring a subscription covered by the still-enabled union needs no control call.
        dispatcher.UpdateSubscriptions([first.Registration, removed.Registration], Context,
            _ => throw new InvalidOperationException("Denied reduction must retain the native flags."));
        EmitProcessStart(dispatcher);
        Assert.Equal(2, first.Received);
        Assert.Equal(1, removed.Received);
    }

    [Fact]
    public void WithdrawingAllCallbacksDoesNotWriteKeywordsAndAllowsRebindingOnTheSameSession()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        dispatcher.UpdateSubscriptions([], Context, _ => throw new InvalidOperationException("No control call expected."));
        EmitProcessStart(dispatcher);
        Assert.Equal(0, first.Received);
        Assert.False(first.Source!.IsActive);
        dispatcher.UpdateSubscriptions([first.Registration], Context, _ => throw new InvalidOperationException("Flags remain enabled."));
        EmitProcessStart(dispatcher);
        Assert.Equal(1, first.Received);
    }

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
            Assert.Null(second.Source);
            Assert.Equal(0, second.BindCount);
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
    public void FailedBindingAfterKeywordUpdateDoesNotChangeThePublishedSet()
    {
        var dispatcher = new KernelEtwSubscriptionDispatcher();
        var first = new Consumer(1, KernelTraceEventParser.Keywords.Process);
        dispatcher.InitializeSubscriptions([first.Registration], Context);
        var failure = new InvalidOperationException("fixture binding failure");
        var invalid = new KernelEtwSubscriptionRegistration(2, "invalid",
            KernelTraceEventParser.Keywords.NetworkTCPIP, (_, _) => throw failure);

        var updates = new List<KernelTraceEventParser.Keywords>();
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => dispatcher.UpdateSubscriptions(
            [first.Registration, invalid], Context, updates.Add)));
        EmitProcessStart(dispatcher);

        Assert.Equal(1, first.Received);
        Assert.True(first.Source!.IsActive);
        Assert.Equal(new[] { KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.NetworkTCPIP }, updates);
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
        Assert.Null(second.Source);
        Assert.Equal(0, second.BindCount);
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
        => dispatcher.Dispatch<ProcessTraceData>(KernelEtwEventKind.ProcessStartGroup, null!, static (source, data) => source.DeliverProcessStartGroup(data));

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
