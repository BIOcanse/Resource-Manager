using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwSessionBrokerTests
{
    [Fact]
    public void Subscribe_MergesKeywordsAndRestartsOneOwnedRuntime()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(
            factory,
            NullLogger<KernelEtwSessionBroker>.Instance);

        var cpu = broker.Subscribe(new KernelEtwSubscriptionRequest(
            "cpu",
            KernelTraceEventParser.Keywords.ContextSwitch,
            static (_, _) => { }));

        Assert.Single(factory.Calls);
        Assert.Equal(KernelTraceEventParser.Keywords.ContextSwitch, factory.Calls[0].Keywords);
        Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(1, broker.GetSnapshot().Generation);

        var network = broker.Subscribe(new KernelEtwSubscriptionRequest(
            "network",
            KernelTraceEventParser.Keywords.NetworkTCPIP,
            static (_, _) => { }));

        Assert.Equal(2, factory.Calls.Count);
        Assert.Equal(
            KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.NetworkTCPIP,
            factory.Calls[1].Keywords);
        Assert.Equal(1, factory.Calls[0].Runtime.StopCount);
        Assert.Equal(2, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(2, broker.GetSnapshot().Generation);

        cpu.Dispose();

        Assert.Equal(3, factory.Calls.Count);
        Assert.Equal(KernelTraceEventParser.Keywords.NetworkTCPIP, factory.Calls[2].Keywords);
        Assert.Equal(1, factory.Calls[1].Runtime.StopCount);
        Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(3, broker.GetSnapshot().Generation);

        network.Dispose();

        Assert.Equal(3, factory.Calls.Count);
        Assert.Equal(1, factory.Calls[2].Runtime.StopCount);
        Assert.Equal("Idle", broker.GetSnapshot().State);
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
    }

    [Fact]
    public void DisposeSubscription_IsIdempotent()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(
            factory,
            NullLogger<KernelEtwSessionBroker>.Instance);
        var subscription = broker.Subscribe(new KernelEtwSubscriptionRequest(
            "file",
            KernelTraceEventParser.Keywords.FileIO,
            static (_, _) => { }));

        subscription.Dispose();
        subscription.Dispose();

        Assert.Single(factory.Calls);
        Assert.Equal(1, factory.Calls[0].Runtime.StopCount);
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
    }

    [Fact]
    public async Task LostEventsReplaceTheRuntimeWithANewGeneration()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(
            factory,
            NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromMilliseconds(10));
        await broker.StartAsync(CancellationToken.None);
        try
        {
            using var subscription = broker.Subscribe(new KernelEtwSubscriptionRequest(
                "cpu",
                KernelTraceEventParser.Keywords.ContextSwitch,
                static (_, _) => { }));
            var first = Assert.Single(factory.Calls);

            first.Runtime.EventsLost = 1;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (factory.Calls.Count < 2)
            {
                await Task.Delay(10, timeout.Token);
            }

            Assert.Equal(1, first.Runtime.StopCount);
            Assert.Equal(2, broker.GetSnapshot().Generation);
            Assert.Equal(0, broker.GetSnapshot().EventsLost);
            Assert.True(broker.GetSnapshot().IsRunning);
            await Task.Delay(75);
            Assert.Equal(2, factory.Calls.Count);
            Assert.Equal(0, factory.Calls[1].Runtime.StopCount);
        }
        finally
        {
            await broker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EventsLostBeforeTheRuntimeStartsProcessingAreNotAcceptedAsABaseline()
    {
        var factory = new FakeRuntimeFactory { FirstRuntimeEventsLost = 1 };
        using var broker = new KernelEtwSessionBroker(
            factory,
            NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromMilliseconds(10));
        await broker.StartAsync(CancellationToken.None);
        try
        {
            using var subscription = broker.Subscribe(new KernelEtwSubscriptionRequest(
                "cpu",
                KernelTraceEventParser.Keywords.ContextSwitch,
                static (_, _) => { }));
            var first = Assert.Single(factory.Calls);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (factory.Calls.Count < 2)
            {
                await Task.Delay(10, timeout.Token);
            }

            Assert.Equal(1, first.Runtime.StopCount);
            Assert.Equal(2, broker.GetSnapshot().Generation);
            Assert.Equal(0, broker.GetSnapshot().EventsLost);
            Assert.True(broker.GetSnapshot().IsRunning);
            await Task.Delay(75);
            Assert.Equal(2, factory.Calls.Count);
            Assert.Equal(0, factory.Calls[1].Runtime.StopCount);
        }
        finally
        {
            await broker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopIsTerminalForLateLeaseReleaseAndNewSubscriptions()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(
            factory,
            NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromMilliseconds(10));
        await broker.StartAsync(CancellationToken.None);
        var subscription = broker.Subscribe(new KernelEtwSubscriptionRequest(
            "cpu",
            KernelTraceEventParser.Keywords.ContextSwitch,
            static (_, _) => { }));

        await broker.StopAsync(CancellationToken.None);

        var stopped = broker.GetSnapshot();
        Assert.Equal("Stopped", stopped.State);
        Assert.Equal(0, stopped.SubscriptionCount);
        Assert.Equal(KernelTraceEventParser.Keywords.None, stopped.EnabledKeywords);
        Assert.Equal(1, Assert.Single(factory.Calls).Runtime.StopCount);

        subscription.Dispose();
        var afterLateRelease = broker.GetSnapshot();
        Assert.Equal("Stopped", afterLateRelease.State);
        Assert.Equal(0, afterLateRelease.SubscriptionCount);
        Assert.Single(factory.Calls);

        Assert.Throws<ObjectDisposedException>(() => broker.Subscribe(
            new KernelEtwSubscriptionRequest(
                "late",
                KernelTraceEventParser.Keywords.Process,
                static (_, _) => { })));
        Assert.Equal("Stopped", broker.GetSnapshot().State);
        Assert.Single(factory.Calls);
    }

    [Fact]
    public async Task FailedRuntimeReleaseCannotBeReportedAsStopped()
    {
        var failedStop = new KernelEtwSessionStopResult(
            SessionStopSucceeded: false,
            SessionDisposeSucceeded: true,
            ProcessingCompleted: true,
            Failures: ["SessionStop: fixture failure."]);
        var factory = new FakeRuntimeFactory { RuntimeStopResult = failedStop };
        using var broker = new KernelEtwSessionBroker(
            factory,
            NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromMilliseconds(10));
        await broker.StartAsync(CancellationToken.None);
        using var subscription = broker.Subscribe(new KernelEtwSubscriptionRequest(
            "cpu",
            KernelTraceEventParser.Keywords.ContextSwitch,
            static (_, _) => { }));

        await broker.StopAsync(CancellationToken.None);

        var stopped = broker.GetSnapshot();
        Assert.Equal("StopFailed", stopped.State);
        Assert.Contains("fixture failure", stopped.Message, StringComparison.Ordinal);
        Assert.Equal(0, stopped.SubscriptionCount);
        Assert.Equal(1, Assert.Single(factory.Calls).Runtime.StopCount);
    }

    [Fact]
    public async Task FailedLostEventRestartBlocksEveryReplacementRuntime()
    {
        var failedStop = new KernelEtwSessionStopResult(
            SessionStopSucceeded: false,
            SessionDisposeSucceeded: true,
            ProcessingCompleted: true,
            Failures: ["SessionStop: fixture restart failure."]);
        var factory = new FakeRuntimeFactory { RuntimeStopResult = failedStop };
        using var broker = new KernelEtwSessionBroker(
            factory,
            NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromMilliseconds(10));
        await broker.StartAsync(CancellationToken.None);
        var firstSubscription = broker.Subscribe(new KernelEtwSubscriptionRequest(
            "cpu",
            KernelTraceEventParser.Keywords.ContextSwitch,
            static (_, _) => { }));
        var first = Assert.Single(factory.Calls);

        first.Runtime.EventsLost = 1;
        await WaitUntilAsync(() => broker.GetSnapshot().State == "StopFailed");
        await Task.Delay(75);

        Assert.Single(factory.Calls);
        Assert.Equal(1, first.Runtime.StopCount);
        Assert.Contains("restart failure", broker.GetSnapshot().Message, StringComparison.Ordinal);

        var secondSubscription = broker.Subscribe(new KernelEtwSubscriptionRequest(
            "process",
            KernelTraceEventParser.Keywords.Process,
            static (_, _) => { }));
        secondSubscription.Dispose();
        firstSubscription.Dispose();
        await Task.Delay(25);

        Assert.Single(factory.Calls);
        Assert.Equal(1, first.Runtime.StopCount);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailedStartCleanupBlocksEveryReplacementRuntime()
    {
        var failedStop = new KernelEtwSessionStopResult(
            SessionStopSucceeded: false,
            SessionDisposeSucceeded: true,
            ProcessingCompleted: true,
            Failures: ["SessionStop: fixture failed-start cleanup failure."]);
        var factory = new FakeRuntimeFactory
        {
            RuntimeStopResult = failedStop,
            FirstRuntimeStartFailure = new InvalidOperationException("fixture start failure"),
        };
        using var broker = new KernelEtwSessionBroker(
            factory,
            NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromMilliseconds(10));
        await broker.StartAsync(CancellationToken.None);
        var firstSubscription = broker.Subscribe(new KernelEtwSubscriptionRequest(
            "cpu",
            KernelTraceEventParser.Keywords.ContextSwitch,
            static (_, _) => { }));

        await WaitUntilAsync(() => broker.GetSnapshot().State == "StopFailed");
        await Task.Delay(75);
        var first = Assert.Single(factory.Calls);
        Assert.Equal(1, first.Runtime.StopCount);
        Assert.Contains("failed-start cleanup failure", broker.GetSnapshot().Message, StringComparison.Ordinal);

        var secondSubscription = broker.Subscribe(new KernelEtwSubscriptionRequest(
            "thread",
            KernelTraceEventParser.Keywords.Thread,
            static (_, _) => { }));
        secondSubscription.Dispose();
        firstSubscription.Dispose();
        await Task.Delay(25);

        Assert.Single(factory.Calls);
        Assert.Equal(1, first.Runtime.StopCount);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnprovenPartialCreationCleanupBlocksRetriesAndShutdownSuccess()
    {
        var creationCleanupFailure = new KernelEtwSessionCreationCleanupException(
            new InvalidOperationException("fixture provider enable failure"),
            new InvalidOperationException("fixture partial cleanup failure"));
        var factory = new FakeRuntimeFactory
        {
            FirstCreateFailure = creationCleanupFailure,
        };
        using var broker = new KernelEtwSessionBroker(
            factory,
            NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromMilliseconds(10));
        await broker.StartAsync(CancellationToken.None);
        var firstSubscription = broker.Subscribe(new KernelEtwSubscriptionRequest(
            "cpu",
            KernelTraceEventParser.Keywords.ContextSwitch,
            static (_, _) => { }));

        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        Assert.Equal(1, factory.CreateAttempts);
        await Task.Delay(75);
        Assert.Equal(1, factory.CreateAttempts);

        var secondSubscription = broker.Subscribe(new KernelEtwSubscriptionRequest(
            "thread",
            KernelTraceEventParser.Keywords.Thread,
            static (_, _) => { }));
        secondSubscription.Dispose();
        firstSubscription.Dispose();
        await Task.Delay(25);

        Assert.Equal(1, factory.CreateAttempts);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        await broker.StopAsync(CancellationToken.None);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
    }

    private sealed class FakeRuntimeFactory : IKernelEtwSessionRuntimeFactory
    {
        private readonly object gate = new();
        private readonly List<CreateCall> calls = [];
        private int createAttempts;
        public IReadOnlyList<CreateCall> Calls
        {
            get
            {
                lock (gate)
                {
                    return calls.ToArray();
                }
            }
        }
        public int CreateAttempts => Volatile.Read(ref createAttempts);
        public int FirstRuntimeEventsLost { get; init; }
        public Exception? FirstRuntimeStartFailure { get; init; }
        public Exception? FirstCreateFailure { get; init; }
        public KernelEtwSessionStopResult RuntimeStopResult { get; init; } =
            KernelEtwSessionStopResult.Released;

        public bool TryGetAvailability(out string state, out string message)
        {
            state = "Available";
            message = "available";
            return true;
        }

        public IKernelEtwSessionRuntime Create(
            string sessionName,
            KernelTraceEventParser.Keywords keywords,
            KernelEtwSessionContext context,
            IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
        {
            lock (gate)
            {
                var attempt = Interlocked.Increment(ref createAttempts);
                if (attempt == 1 && FirstCreateFailure is not null)
                {
                    throw FirstCreateFailure;
                }

                var runtime = new FakeRuntime
                {
                    EventsLost = attempt == 1 ? FirstRuntimeEventsLost : 0,
                    StartFailure = attempt == 1 ? FirstRuntimeStartFailure : null,
                    StopResult = RuntimeStopResult,
                };
                calls.Add(new CreateCall(
                    sessionName,
                    keywords,
                    context,
                    subscriptions.Select(static item => item.Id).ToArray(),
                    runtime));
                return runtime;
            }
        }
    }

    private sealed class FakeRuntime : IKernelEtwSessionRuntime
    {
        private int isProcessing;
        private int eventsLost;
        private int stopCount;
        public bool IsProcessing => Volatile.Read(ref isProcessing) != 0;
        public int EventsLost
        {
            get => Volatile.Read(ref eventsLost);
            set => Volatile.Write(ref eventsLost, value);
        }
        public int StopCount => Volatile.Read(ref stopCount);
        public Exception? StartFailure { get; init; }
        public KernelEtwSessionStopResult StopResult { get; init; } =
            KernelEtwSessionStopResult.Released;

        public void StartProcessing(Action<Exception?> completed)
        {
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            Volatile.Write(ref isProcessing, 1);
        }

        public KernelEtwSessionStopResult Stop(TimeSpan waitTimeout)
        {
            if (!IsProcessing && StopCount > 0)
            {
                return StopResult;
            }

            Volatile.Write(ref isProcessing, 0);
            Interlocked.Increment(ref stopCount);
            return StopResult;
        }

        public void Dispose()
        {
            _ = Stop(TimeSpan.Zero);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed record CreateCall(
        string SessionName,
        KernelTraceEventParser.Keywords Keywords,
        KernelEtwSessionContext Context,
        IReadOnlyList<string> SubscriptionIds,
        FakeRuntime Runtime);
}
