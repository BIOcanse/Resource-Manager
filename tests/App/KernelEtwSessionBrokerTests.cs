using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwSessionBrokerTests
{
    [Fact]
    public void DeniedKeywordUpdatesWarnOnlyWhenTheFailureChangesOrAfterRecovery()
    {
        var diagnostics = new List<KernelEtwDiagnostic>();
        var factory = new FakeRuntimeFactory();
        using var owner = new KernelEtwSessionOwner(factory, new(diagnostics.Add));
        using var cpu = owner.Subscribe(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        var runtime = Assert.Single(factory.Calls).Runtime;
        runtime.UpdateFailure = new UnauthorizedAccessException("first denial");
        using var disk = owner.Subscribe(new("disk", KernelTraceEventParser.Keywords.DiskIO, static (_, _) => { }));
        owner.Reconcile();
        owner.Reconcile();
        Assert.Single(diagnostics, item => item.Level == KernelEtwDiagnosticLevel.Warning);
        Assert.Equal(2, diagnostics.Count(item => item.Level == KernelEtwDiagnosticLevel.Debug));

        runtime.UpdateFailure = new UnauthorizedAccessException("different denial");
        owner.Reconcile();
        Assert.Equal(2, diagnostics.Count(item => item.Level == KernelEtwDiagnosticLevel.Warning));
        Assert.Contains("different denial", disk.GetSnapshot().Message);

        runtime.UpdateFailure = null;
        owner.Reconcile();
        runtime.UpdateFailure = new UnauthorizedAccessException("different denial");
        using var network = owner.Subscribe(new("network", KernelTraceEventParser.Keywords.NetworkTCPIP, static (_, _) => { }));
        Assert.Equal(3, diagnostics.Count(item => item.Level == KernelEtwDiagnosticLevel.Warning));
        Assert.Equal("Unavailable", network.GetSnapshot().State);
        Assert.True(disk.GetSnapshot().IsRunning);
        Assert.Equal(0, runtime.StopCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeniedStopRetriesLogErrorsOnlyWhenTheFailureChanges(bool restart)
    {
        var diagnostics = new List<KernelEtwDiagnostic>();
        var factory = new FakeRuntimeFactory();
        using var owner = new KernelEtwSessionOwner(factory, new(diagnostics.Add));
        using var cpu = owner.Subscribe(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        var runtime = Assert.Single(factory.Calls).Runtime;
        runtime.StopResult = new(false, false, false, ["SessionStop: fixture denied"]) { ControlDenied = true };
        if (restart) runtime.UpdateFailure = new InvalidOperationException("fixture update requires release");
        using var disk = restart
            ? owner.Subscribe(new("disk", KernelTraceEventParser.Keywords.DiskIO, static (_, _) => { }))
            : null;
        if (!restart) cpu.Dispose();
        owner.Reconcile();
        owner.Reconcile();

        Assert.Single(diagnostics, item => item.Level == KernelEtwDiagnosticLevel.Error);
        Assert.DoesNotContain(diagnostics, item => item.Level == KernelEtwDiagnosticLevel.Warning);
        Assert.True(diagnostics.Count(item => item.Level == KernelEtwDiagnosticLevel.Debug) >= 2);
        Assert.Equal(3, runtime.StopCount);
        Assert.True(owner.GetSnapshot().IsRunning);
        Assert.Single(factory.Calls);

        runtime.StopResult = new(false, false, false, ["SessionStop: different denial"]) { ControlDenied = true };
        owner.Reconcile();
        Assert.Equal(2, diagnostics.Count(item => item.Level == KernelEtwDiagnosticLevel.Error));
        Assert.Contains("different denial", owner.GetSnapshot().Message);
        runtime.UpdateFailure = null;
        runtime.StopResult = KernelEtwSessionStopResult.Released;
        owner.Reconcile();
        Assert.Equal(restart ? "Running" : "Idle", owner.GetSnapshot().State);
    }

    [Fact]
    public void LeaseAvailabilityIncludesNativeMetadataFlagsAndEndsOnRelease()
    {
        var factory = new FakeRuntimeFactory();
        using var owner = new KernelEtwSessionOwner(factory);
        using var network = owner.Subscribe(new("network", KernelTraceEventParser.Keywords.NetworkTCPIP, static (_, _) => { }));
        var runtime = Assert.Single(factory.Calls).Runtime;
        using var metadata = owner.Subscribe(new("metadata", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread,
            static (_, _) => { }));
        Assert.True(metadata.GetSnapshot().IsRunning);
        runtime.StopResult = new(false, false, false, ["SessionStop: fixture denied"]) { ControlDenied = true };
        metadata.Dispose();
        Assert.Equal("Idle", metadata.GetSnapshot().State);
        Assert.True(network.GetSnapshot().IsRunning);
        network.Dispose();
        Assert.True(owner.GetSnapshot().IsRunning);
        Assert.Equal("Idle", network.GetSnapshot().State);
        runtime.StopResult = KernelEtwSessionStopResult.Released;
        owner.Reconcile();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeniedUpdateKeepsExistingDeliveryAndRetriesOnlyPendingKeywords(bool win32)
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        var received = 0;
        using var first = broker.Subscribe(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch,
            (source, _) => source.ThreadCSwitch += _ => received++));
        var original = Assert.Single(factory.Calls).Runtime;
        var before = broker.GetSnapshot();
        original.UpdateFailure = win32 ? new System.ComponentModel.Win32Exception(5, "fixture access denied")
            : new UnauthorizedAccessException("fixture access denied");
        using var pending = broker.Subscribe(new("disk", KernelTraceEventParser.Keywords.DiskIO, static (_, _) => { }));
        var sameKeywordReceived = 0;
        using var covered = broker.Subscribe(new("cpu-2", KernelTraceEventParser.Keywords.ContextSwitch,
            (source, _) => source.ThreadCSwitch += _ => sameKeywordReceived++));
        broker.Owner.Reconcile();
        original.EmitContextSwitch();

        Assert.Equal(1, received);
        Assert.Equal(1, sameKeywordReceived);
        Assert.Single(factory.Calls);
        Assert.Equal(0, original.StopCount);
        var denied = broker.GetSnapshot();
        Assert.Equal(before.Generation, denied.Generation);
        Assert.Equal(before.StartedAt, denied.StartedAt);
        Assert.Equal(before.EnabledKeywords, denied.EnabledKeywords);
        Assert.True(first.GetSnapshot().IsRunning);
        Assert.True(covered.GetSnapshot().IsRunning);
        Assert.Equal("Unavailable", pending.GetSnapshot().State);
        Assert.Contains("fixture access denied", pending.GetSnapshot().Message);

        original.UpdateFailure = null;
        broker.Owner.Reconcile();
        Assert.True(pending.GetSnapshot().IsRunning);
        Assert.Equal(before.Generation, broker.GetSnapshot().Generation);
        Assert.Equal(0, original.StopCount);
    }

    [Fact]
    public void DeniedLastStopKeepsOwnershipAndCanServeAgainBeforeStopRetrySucceeds()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        var first = broker.Subscribe(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        var runtime = Assert.Single(factory.Calls).Runtime;
        var before = broker.GetSnapshot();
        runtime.StopResult = new(false, false, false, ["SessionStop: UnauthorizedAccessException: fixture denied"])
        { ControlDenied = true };
        first.Dispose();
        broker.Owner.Reconcile();
        Assert.True(runtime.IsProcessing);
        Assert.True(broker.GetSnapshot().IsRunning);
        Assert.Equal(before.EnabledKeywords, broker.GetSnapshot().EnabledKeywords);
        Assert.Contains("fixture denied", broker.GetSnapshot().Message);
        Assert.Equal(2, runtime.StopCount);

        var received = 0;
        var second = broker.Subscribe(new("cpu-2", KernelTraceEventParser.Keywords.ContextSwitch,
            (source, _) => source.ThreadCSwitch += _ => received++));
        runtime.EmitContextSwitch();
        Assert.Equal(1, received);
        Assert.Single(factory.Calls);
        Assert.Equal(before.Generation, broker.GetSnapshot().Generation);
        second.Dispose();
        runtime.StopResult = KernelEtwSessionStopResult.Released;
        broker.Owner.Reconcile();
        Assert.False(runtime.IsProcessing);
        Assert.Equal("Idle", broker.GetSnapshot().State);
        Assert.Single(factory.Calls);
    }

    [Fact]
    public void DiskConsumerReportsTheKeywordDenialWhileCpuContinues()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        var received = 0;
        using var cpu = broker.Subscribe(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch,
            (source, _) => source.ThreadCSwitch += _ => received++));
        var runtime = Assert.Single(factory.Calls).Runtime;
        runtime.UpdateFailure = new System.ComponentModel.Win32Exception(5, "fixture disk control denied");
        using var disk = new ResourceManager.ReadWrite.Windows.Processes.EtwPhysicalDiskIoCaptureOwner(broker);

        var denied = disk.Read();
        Assert.Equal(ResourceManager.App.Application.ResourceBreakdown.PhysicalDiskIoProviderStates.Unavailable, denied.ProviderState.State);
        Assert.Contains("fixture disk control denied", denied.ProviderState.Message);
        Assert.False(disk.IsRunning);
        runtime.EmitContextSwitch();
        Assert.Equal(1, received);
        Assert.Equal(0, runtime.StopCount);

        runtime.UpdateFailure = null;
        broker.Owner.Reconcile();
        Assert.True(disk.IsRunning);
        Assert.NotEqual(ResourceManager.App.Application.ResourceBreakdown.PhysicalDiskIoProviderStates.Unavailable, disk.Read().ProviderState.State);
        Assert.Single(factory.Calls);
    }

    [Fact]
    public void DeniedRestartStopKeepsServingActiveConsumersWithoutBlockingReconciliation()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        var received = 0;
        using var cpu = broker.Subscribe(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch,
            (source, _) => source.ThreadCSwitch += _ => received++));
        var runtime = Assert.Single(factory.Calls).Runtime;
        runtime.UpdateFailure = new InvalidOperationException("fixture update failure requires release");
        runtime.StopResult = new(false, false, false, ["SessionStop: fixture access denied"])
        { ControlDenied = true };
        using var disk = broker.Subscribe(new("disk", KernelTraceEventParser.Keywords.DiskIO, static (_, _) => { }));
        runtime.EmitContextSwitch();
        Assert.Equal(1, received);
        Assert.True(broker.GetSnapshot().IsRunning);
        Assert.Equal(1, runtime.StopCount);
        Assert.Single(factory.Calls);

        runtime.UpdateFailure = null;
        runtime.StopResult = KernelEtwSessionStopResult.Released;
        broker.Owner.Reconcile();
        Assert.True(disk.GetSnapshot().IsRunning);
        Assert.Equal(1, runtime.StopCount);
        Assert.Single(factory.Calls);
    }

    [Fact]
    public void FailedSubscriptionUpdateReleasesTheRuntimeBeforeCreatingItsReplacement()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        using var first = broker.Subscribe(new("process", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
        var original = Assert.Single(factory.Calls).Runtime;
        original.UpdateFailure = new InvalidOperationException("fixture subscription update failure");

        using var second = broker.Subscribe(new("network", KernelTraceEventParser.Keywords.NetworkTCPIP, static (_, _) => { }));

        Assert.Equal(1, original.StopCount);
        Assert.Equal(2, factory.Calls.Count);
        Assert.Equal(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.NetworkTCPIP,
            factory.Calls[1].Keywords);
        Assert.Equal(2, broker.GetSnapshot().Generation);
        Assert.True(broker.GetSnapshot().IsRunning);
    }

    [Fact]
    public void FailedSubscriptionUpdateWithUnprovenReleaseBlocksEveryReplacement()
    {
        var factory = new FakeRuntimeFactory
        {
            RuntimeStopResult = new(false, true, true, ["fixture update cleanup failure"])
        };
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        using var first = broker.Subscribe(new("process", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
        var original = Assert.Single(factory.Calls).Runtime;
        original.UpdateFailure = new InvalidOperationException("fixture subscription update failure");

        using var second = broker.Subscribe(new("network", KernelTraceEventParser.Keywords.NetworkTCPIP, static (_, _) => { }));
        using var third = broker.Subscribe(new("files", KernelTraceEventParser.Keywords.FileIO, static (_, _) => { }));

        Assert.Single(factory.Calls);
        Assert.Equal(1, original.StopCount);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        Assert.Contains("fixture update cleanup failure", broker.GetSnapshot().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Subscribe_MergesKeywordsWithoutReplacingTheOwnedRuntime()
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

        Assert.Single(factory.Calls);
        Assert.Equal(
            KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.NetworkTCPIP,
            Assert.Single(factory.Calls[0].Runtime.Updates));
        Assert.Equal(0, factory.Calls[0].Runtime.StopCount);
        Assert.Equal(2, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(1, broker.GetSnapshot().Generation);
        Assert.Equal(factory.Calls[0].Context.StartedAt, broker.GetSnapshot().StartedAt);

        cpu.Dispose();

        Assert.Single(factory.Calls);
        Assert.Equal(KernelTraceEventParser.Keywords.NetworkTCPIP, factory.Calls[0].Runtime.Updates[1]);
        Assert.Equal(0, factory.Calls[0].Runtime.StopCount);
        Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(1, broker.GetSnapshot().Generation);

        network.Dispose();

        Assert.Single(factory.Calls);
        Assert.Equal(1, factory.Calls[0].Runtime.StopCount);
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
    public async Task LostEventsPreserveTheRuntimeAndRemainVisibleToConsumers()
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
            await Task.Delay(75);
            Assert.Single(factory.Calls);
            Assert.Equal(0, first.Runtime.StopCount);
            Assert.Equal(first.Context.Generation, broker.GetSnapshot().Generation);
            Assert.Equal(first.Context.StartedAt, broker.GetSnapshot().StartedAt);
            Assert.Equal(1, broker.GetSnapshot().EventsLost);
            Assert.True(broker.GetSnapshot().IsRunning);

            using var network = broker.Subscribe(new("network", KernelTraceEventParser.Keywords.NetworkTCPIP,
                static (_, _) => { }));
            Assert.Single(factory.Calls);
            Assert.Single(first.Runtime.Updates);
            Assert.Equal(1, broker.GetSnapshot().EventsLost);
            Assert.Equal(0, first.Runtime.StopCount);
        }
        finally
        {
            await broker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InitialEventsLostRemainVisibleWithoutRepeatedSessionRecreation()
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

            await Task.Delay(75);
            Assert.Single(factory.Calls);
            Assert.Equal(0, first.Runtime.StopCount);
            Assert.Equal(first.Context.Generation, broker.GetSnapshot().Generation);
            Assert.Equal(1, broker.GetSnapshot().EventsLost);
            Assert.True(broker.GetSnapshot().IsRunning);
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
    public async Task FailedProcessingRestartBlocksEveryReplacementRuntime()
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

        first.Runtime.StopProcessingUnexpectedly();
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
                runtime.Dispatcher.InitializeSubscriptions(subscriptions, context);
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
        public List<KernelTraceEventParser.Keywords> Updates { get; } = [];
        public KernelEtwSubscriptionDispatcher Dispatcher { get; } = new();
        public void EmitContextSwitch() => Dispatcher.Dispatch<Microsoft.Diagnostics.Tracing.Parsers.Kernel.CSwitchTraceData>(
            null!, static (source, data) => source.DeliverThreadCSwitch(data));
        public Exception? UpdateFailure { get; set; }
        public bool IsProcessing => Volatile.Read(ref isProcessing) != 0;
        public int EventsLost
        {
            get => Volatile.Read(ref eventsLost);
            set => Volatile.Write(ref eventsLost, value);
        }
        public int StopCount => Volatile.Read(ref stopCount);
        public Exception? StartFailure { get; init; }
        public KernelEtwSessionStopResult StopResult { get; set; } =
            KernelEtwSessionStopResult.Released;

        public void UpdateSubscriptions(
            IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions,
            KernelEtwSessionContext context)
        {
            Assert.True(IsProcessing);
            Dispatcher.UpdateSubscriptions(subscriptions, context, _ =>
            {
                if (UpdateFailure is not null) throw UpdateFailure;
            });
            Updates.Add(subscriptions.Aggregate(KernelTraceEventParser.Keywords.None,
                static (all, subscription) => all | subscription.Keywords));
        }

        public void StartProcessing(Action<Exception?> completed)
        {
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            Volatile.Write(ref isProcessing, 1);
        }

        public void StopProcessingUnexpectedly() => Volatile.Write(ref isProcessing, 0);

        public KernelEtwSessionStopResult Stop(TimeSpan waitTimeout)
        {
            if (!IsProcessing && StopCount > 0)
            {
                return StopResult;
            }

            Interlocked.Increment(ref stopCount);
            if (!StopResult.SessionStopSucceeded && !StopResult.ProcessingCompleted) return StopResult;
            Dispatcher.CancelAll();
            Volatile.Write(ref isProcessing, 0);
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
