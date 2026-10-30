using Resource_Manager_APP.Tests.KernelEtwFixtures;
using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwSessionBrokerTests
{
    [Fact]
    public async Task FailedAcquisitionRollsBackTheUndeliveredHolderBeforeAnotherAcquisition()
    {
        var factory = new FakeRuntimeFactory { AvailabilityFailure = new InvalidOperationException("fixture reconciliation failure") };
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromMilliseconds(10));

        Assert.Throws<InvalidOperationException>(() => broker.Acquire(
            new("failed", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { })));
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(1, factory.AvailabilityChecks);
        await Task.Delay(75);
        Assert.Equal(1, factory.AvailabilityChecks);

        factory.AvailabilityFailure = null;
        using var successful = broker.Acquire(new("successful", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
        Assert.Equal(new[] { "successful" }, Assert.Single(factory.Calls).SubscriptionIds);
        successful.Dispose();
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal("Idle", broker.GetSnapshot().State);
    }

    [Fact]
    public void FailedAcquisitionDuringRuntimeDisposalRollsBackWithoutRetryingCleanup()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        using var held = broker.Acquire(new("held", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
        var runtime = Assert.Single(factory.Calls).Runtime;
        runtime.UpdateFailure = new InvalidOperationException("fixture update requires replacement");
        runtime.DisposalFailure = new InvalidOperationException("fixture partial disposal failure");

        Assert.Throws<InvalidOperationException>(() => broker.Acquire(
            new("failed", KernelTraceEventParser.Keywords.NetworkTCPIP, static (_, _) => { })));
        Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(1, runtime.DisposalAttempts);
        Assert.Equal("StopFailed", held.GetSnapshot().State);
        Assert.Single(factory.Calls);

        runtime.DisposalFailure = null;
        broker.Owner.Reconcile();
        Assert.True(runtime.DisposalCompleted);
        Assert.Equal(2, runtime.DisposalAttempts);
        Assert.Single(factory.Calls);
        broker.Owner.Reconcile();
        Assert.Equal(2, factory.Calls.Count);
        Assert.Equal(new[] { "held" }, factory.Calls[1].SubscriptionIds);
        Assert.True(held.GetSnapshot().IsRunning);
    }

    [Fact]
    public void FailedInitialAcquisitionRetainsPartialCleanupWithoutLeavingAHolder()
    {
        var factory = new FakeRuntimeFactory
        {
            FirstRuntimeStartFailure = new InvalidOperationException("fixture start failure"),
            FirstRuntimeDisposalFailure = new InvalidOperationException("fixture failed-start disposal failure")
        };
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        Assert.Throws<InvalidOperationException>(() => broker.Acquire(
            new("failed", KernelTraceEventParser.Keywords.Process, static (_, _) => { })));
        var runtime = Assert.Single(factory.Calls).Runtime;
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        Assert.Equal(1, runtime.DisposalAttempts);
        runtime.DisposalFailure = null;
        broker.Owner.Reconcile();
        Assert.Equal(2, runtime.DisposalAttempts);
        Assert.True(runtime.DisposalCompleted);
        Assert.Equal("Idle", broker.GetSnapshot().State);
        Assert.Single(factory.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedFinalDisposalRetainsItsCleanupObligationAndBlocksReplacement(bool shutdown)
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        using var held = broker.Acquire(new("held", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
        var runtime = Assert.Single(factory.Calls).Runtime;
        runtime.DisposalFailure = new InvalidOperationException("fixture partial disposal failure");

        Action release = shutdown ? broker.Dispose : held.Dispose;
        Assert.Throws<InvalidOperationException>(release);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(1, runtime.DisposalAttempts);
        Assert.False(runtime.DisposalCompleted);
        IKernelEtwSubscription? next = null;
        if (!shutdown)
        {
            next = broker.Acquire(new("next", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
            Assert.Equal("StopFailed", next.GetSnapshot().State);
            Assert.Single(factory.Calls);
            Assert.Equal(1, runtime.DisposalAttempts);
        }

        runtime.DisposalFailure = null;
        if (shutdown) broker.Dispose();
        else broker.Owner.Reconcile();
        Assert.Equal(2, runtime.DisposalAttempts);
        Assert.Equal(1, runtime.StopCount);
        Assert.True(runtime.DisposalCompleted);
        Assert.Equal(shutdown ? "Stopped" : "Idle", broker.GetSnapshot().State);
        Assert.Single(factory.Calls);
        if (next is not null)
        {
            broker.Owner.Reconcile();
            Assert.Equal(2, factory.Calls.Count);
            Assert.True(next.GetSnapshot().IsRunning);
            next.Dispose();
        }
    }

    [Fact]
    public void RuntimeCleanupRetriesOnlyTheFailedPartsOnTheNextExplicitDisposal()
    {
        var cleanup = new KernelEtwSessionRuntimeCleanup();
        var attempts = new int[4];
        var failSource = true;
        Action cancel = () => attempts[0]++;
        Action source = () => { attempts[1]++; if (failSource) throw new InvalidOperationException("fixture source handle still held"); };
        Action session = () => attempts[2]++;
        Action join = () => attempts[3]++;

        cleanup.CancelSubscriptions(cancel);
        cleanup.DisposeSession(session);
        cleanup.ConfirmProcessingJoined();
        Assert.Throws<AggregateException>(() => cleanup.Dispose(static () => { }, cancel, static () => { }, source, session, join));
        Assert.Equal(new[] { 1, 1, 1, 0 }, attempts);
        failSource = false;
        cleanup.Dispose(static () => { }, cancel, static () => { }, source, session, join);
        cleanup.Dispose(static () => { }, cancel, static () => { }, source, session, join);
        Assert.Equal(new[] { 1, 2, 1, 0 }, attempts);
    }

    [Fact]
    public void RuntimeCleanupDoesNotRetryAFailedStopStepDuringTheSameRelease()
    {
        var cleanup = new KernelEtwSessionRuntimeCleanup();
        var attempts = 0;
        var failSession = true;
        Action session = () => { attempts++; if (failSession) throw new InvalidOperationException("fixture session handle still held"); };
        Assert.Throws<InvalidOperationException>(() => cleanup.DisposeSession(session));
        Assert.Throws<AggregateException>(() => cleanup.Dispose(static () => { }, static () => { }, static () => { },
            static () => { }, session, static () => { }));
        Assert.Equal(1, attempts);
        failSession = false;
        cleanup.Dispose(static () => { }, static () => { }, static () => { }, static () => { }, session, static () => { });
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RuntimeCleanupRetainsEachFailedStepAndDoesNotRepeatCompletedSteps(int failedStep)
    {
        var cleanup = new KernelEtwSessionRuntimeCleanup();
        var attempts = new int[4];
        var fail = true;
        var actions = Enumerable.Range(0, 4).Select(index => (Action)(() =>
        {
            attempts[index]++;
            if (index == failedStep && fail) throw new InvalidOperationException("fixture pending cleanup");
        })).ToArray();
        Assert.Throws<AggregateException>(() => cleanup.Dispose(static () => { }, actions[0], static () => { }, actions[1], actions[2], actions[3]));
        Assert.Equal(new[] { 1, 1, 1, failedStep == 3 ? 1 : 0 }, attempts);
        fail = false;
        cleanup.Dispose(static () => { }, actions[0], static () => { }, actions[1], actions[2], actions[3]);
        cleanup.Dispose(static () => { }, actions[0], static () => { }, actions[1], actions[2], actions[3]);
        Assert.Equal(Enumerable.Range(0, 4).Select(index => index == failedStep ? 2 : 1), attempts);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void FailedStopDoesNotJoinAndNextExplicitCleanupRetriesStopBeforeJoining(bool nativeStop, bool failedDuringStop)
    {
        var cleanup = new KernelEtwSessionRuntimeCleanup();
        var order = new List<string>();
        var stopAttempts = 0;
        var joins = 0;
        var fail = true;
        Action stop = () =>
        {
            order.Add("stop");
            stopAttempts++;
            if (fail) throw new InvalidOperationException("fixture unblock failed");
        };
        Action join = () => { order.Add("join"); joins++; };
        Action noOp = static () => { };
        Action source = () => order.Add("source");
        Action session = () => order.Add("session");
        if (failedDuringStop)
            Assert.Throws<InvalidOperationException>(() =>
            {
                if (nativeStop) cleanup.StopSession(stop);
                else cleanup.StopProcessing(stop);
            });

        Assert.Throws<AggregateException>(() => cleanup.Dispose(nativeStop ? stop : noOp, noOp,
            nativeStop ? noOp : stop, source, session, join));
        Assert.Equal(1, stopAttempts);
        Assert.Equal(0, joins);
        Assert.Equal(new[] { "stop", "source", "session" }, order);

        fail = false;
        cleanup.Dispose(nativeStop ? stop : noOp, noOp, nativeStop ? noOp : stop, source, session, join);
        cleanup.Dispose(nativeStop ? stop : noOp, noOp, nativeStop ? noOp : stop, source, session, join);
        Assert.Equal(2, stopAttempts);
        Assert.Equal(1, joins);
        Assert.Equal(new[] { "stop", "source", "session", "stop", "join" }, order);
    }

    [Fact]
    public void FailedSourceDisposalDoesNotJoinUntilNextExplicitCleanupClosesIt()
    {
        var cleanup = new KernelEtwSessionRuntimeCleanup();
        var order = new List<string>();
        var fail = true;
        Action stop = () => order.Add("stop");
        Action source = () =>
        {
            order.Add("source");
            if (fail) throw new InvalidOperationException("fixture trace handle remains open");
        };
        Action join = () => order.Add("join");
        Action noOp = static () => { };

        Assert.Throws<AggregateException>(() => cleanup.Dispose(noOp, noOp, stop, source, noOp, join));
        Assert.Equal(new[] { "stop", "source" }, order);
        fail = false;
        cleanup.Dispose(noOp, noOp, stop, source, noOp, join);
        cleanup.Dispose(noOp, noOp, stop, source, noOp, join);
        Assert.Equal(new[] { "stop", "source", "source", "join" }, order);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedNativeStopRemainsPendingUntilTheNextExplicitRuntimeDisposal(bool denied)
    {
        var native = new CleanupNativeControl
        {
            Failure = denied ? new UnauthorizedAccessException("fixture native stop denied")
                : new InvalidOperationException("fixture native stop failed")
        };
        using var session = new Microsoft.Diagnostics.Tracing.Session.TraceEventSession(native.Identity.Name)
        { StopOnDispose = false };
        // No source or native session is started; the injected controller owns all
        // native calls, while this exercises the explicit managed-oracle cleanup path. Native runtime cleanup has its own product tests.
        var runtime = new OracleTraceEventSessionProcessingRuntime(session,
            new KernelEtwSessionKeywordControl(native.Identity.Name, native));
        var result = runtime.Stop(TimeSpan.Zero);
        Assert.False(result.IsReleased);
        Assert.Equal(denied, result.ControlDenied);
        Assert.Throws<AggregateException>(runtime.Dispose);
        Assert.Equal(1, native.StopAttempts);

        native.Failure = null;
        runtime.Dispose();
        runtime.Dispose();
        Assert.Equal(2, native.StopAttempts);
    }

    [Fact]
    public void FailedRuntimeStopDoesNotCacheItsFailureAcrossExplicitRetries()
    {
        var native = new CleanupNativeControl { Failure = new InvalidOperationException("fixture native stop failed") };
        using var session = new Microsoft.Diagnostics.Tracing.Session.TraceEventSession(native.Identity.Name)
        { StopOnDispose = false };
        var runtime = new OracleTraceEventSessionProcessingRuntime(session,
            new KernelEtwSessionKeywordControl(native.Identity.Name, native));
        Assert.False(runtime.Stop(TimeSpan.Zero).IsReleased);
        native.Failure = null;
        Assert.True(runtime.Stop(TimeSpan.Zero).IsReleased);
        Assert.True(runtime.Stop(TimeSpan.Zero).IsReleased);
        runtime.Dispose();
        Assert.Equal(2, native.StopAttempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LastHolderReleaseWaitsForConsumerDisposalAfterDeniedStopOrDrainTimeout(bool controlDenied)
    {
        using var disposalRelease = new ManualResetEventSlim();
        var factory = new FakeRuntimeFactory
        {
            RuntimeDisposalRelease = disposalRelease,
            RuntimeStopResult = controlDenied
                ? new(false, false, false, ["SessionStop: fixture denied"]) { ControlDenied = true }
                : new(true, true, false, ["ConsumerThread: fixture drain timeout"])
        };
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        using var subscription = broker.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        var runtime = Assert.Single(factory.Calls).Runtime;
        var release = Task.Run(subscription.Dispose);
        try
        {
            await runtime.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(release.IsCompleted);
            Assert.Equal(1, runtime.StopCount);
        }
        finally
        {
            disposalRelease.Set();
            await release.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(runtime.DisposalCompleted);
        Assert.False(runtime.IsProcessing);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
        Assert.Contains(controlDenied ? "fixture denied" : "fixture drain timeout", broker.GetSnapshot().Message);
        using var next = broker.Acquire(new("next", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
        broker.Owner.Reconcile();
        Assert.Single(factory.Calls);
        Assert.Equal("StopFailed", next.GetSnapshot().State);
    }

    [Fact]
    public void ReacquiringAfterLastReleaseCreatesANewRuntimeWithoutRevivingReleasedReaders()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        Assert.Empty(factory.Calls);
        var received = 0;
        var oldLease = broker.Acquire(new("first", KernelTraceEventParser.Keywords.ContextSwitch,
            (source, _) => source.ObservePublications(() => received++)));
        var firstGeneration = oldLease.GetSnapshot().Generation;
        oldLease.Dispose();
        var oldRuntime = Assert.Single(factory.Calls).Runtime;
        Assert.True(oldRuntime.DisposalCompleted);
        Assert.Equal("Idle", broker.GetSnapshot().State);
        using var nextLease = broker.Acquire(new("second", KernelTraceEventParser.Keywords.DiskIO, static (_, _) => { }));
        Assert.Equal(2, factory.Calls.Count);
        var nextRuntime = factory.Calls[1].Runtime;
        Assert.NotSame(oldRuntime, nextRuntime);
        Assert.True(nextLease.GetSnapshot().IsRunning);
        Assert.True(nextLease.GetSnapshot().Generation > firstGeneration);
        Assert.Equal(new[] { "second" }, factory.Calls[1].SubscriptionIds);
        oldLease.Dispose();
        Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(0, nextRuntime.StopCount);
        nextRuntime.EmitPublicationBoundary();
        Assert.Equal(0, received);
    }

    [Fact]
    public void DisposedBrokerCannotAcquireAnotherResourceAndLateLeaseReleaseIsIdempotent()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        using var subscription = broker.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        broker.Dispose();
        subscription.Dispose();
        broker.Dispose();
        Assert.Single(factory.Calls);
        Assert.True(factory.Calls[0].Runtime.DisposalCompleted);
        Assert.Equal(1, factory.Calls[0].Runtime.StopCount);
        Assert.Equal("Stopped", broker.GetSnapshot().State);
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
        Assert.Throws<ObjectDisposedException>(() => broker.Acquire(
            new("late", KernelTraceEventParser.Keywords.Process, static (_, _) => { })));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrokerDisposalWaitsForTheRetainedConsumer(bool controlDenied)
    {
        using var disposalRelease = new ManualResetEventSlim();
        var factory = new FakeRuntimeFactory
        {
            RuntimeDisposalRelease = disposalRelease,
            RuntimeStopResult = controlDenied
                ? new(false, false, false, ["SessionStop: fixture denied"]) { ControlDenied = true }
                : KernelEtwSessionStopResult.Released
        };
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        using var subscription = broker.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        var runtime = Assert.Single(factory.Calls).Runtime;
        var disposal = Task.Run(broker.Dispose);
        try
        {
            await runtime.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(disposal.IsCompleted);
            Assert.Equal("Stopping", broker.GetSnapshot().State);
        }
        finally
        {
            disposalRelease.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(runtime.DisposalCompleted);
        Assert.False(runtime.IsProcessing);
        Assert.Equal(controlDenied ? "StopFailed" : "Stopped", broker.GetSnapshot().State);
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
    }

    [Fact]
    public void DeniedKeywordUpdatesWarnOnlyWhenTheFailureChangesOrAfterRecovery()
    {
        var diagnostics = new List<KernelEtwDiagnostic>();
        var factory = new FakeRuntimeFactory();
        using var owner = new KernelEtwSessionOwner(factory, new(diagnostics.Add));
        using var cpu = owner.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        var runtime = Assert.Single(factory.Calls).Runtime;
        runtime.UpdateFailure = new UnauthorizedAccessException("first denial");
        using var disk = owner.Acquire(new("disk", KernelTraceEventParser.Keywords.DiskIO, static (_, _) => { }));
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
        using var network = owner.Acquire(new("network", KernelTraceEventParser.Keywords.NetworkTCPIP, static (_, _) => { }));
        Assert.Equal(3, diagnostics.Count(item => item.Level == KernelEtwDiagnosticLevel.Warning));
        Assert.Equal("Unavailable", network.GetSnapshot().State);
        Assert.True(disk.GetSnapshot().IsRunning);
        Assert.Equal(0, runtime.StopCount);
    }

    [Fact]
    public void DeniedRestartStopRetriesLogErrorsOnlyWhenTheFailureChanges()
    {
        var diagnostics = new List<KernelEtwDiagnostic>();
        var factory = new FakeRuntimeFactory();
        using var owner = new KernelEtwSessionOwner(factory, new(diagnostics.Add));
        using var cpu = owner.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        var runtime = Assert.Single(factory.Calls).Runtime;
        runtime.StopResult = new(false, false, false, ["SessionStop: fixture denied"]) { ControlDenied = true };
        runtime.UpdateFailure = new InvalidOperationException("fixture update requires release");
        using var disk = owner.Acquire(new("disk", KernelTraceEventParser.Keywords.DiskIO, static (_, _) => { }));
        owner.Reconcile();
        owner.Reconcile();

        Assert.Single(diagnostics, item => item.Level == KernelEtwDiagnosticLevel.Error);
        Assert.NotNull(diagnostics.Single(item => item.Level == KernelEtwDiagnosticLevel.Error).Exception);
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
        Assert.Equal("Running", owner.GetSnapshot().State);
    }

    [Fact]
    public void LeaseAvailabilityIncludesNativeMetadataFlagsAndEndsOnRelease()
    {
        var factory = new FakeRuntimeFactory();
        using var owner = new KernelEtwSessionOwner(factory);
        using var network = owner.Acquire(new("network", KernelTraceEventParser.Keywords.NetworkTCPIP, static (_, _) => { }));
        var runtime = Assert.Single(factory.Calls).Runtime;
        using var metadata = owner.Acquire(new("metadata", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread,
            static (_, _) => { }));
        Assert.True(metadata.GetSnapshot().IsRunning);
        runtime.StopResult = new(false, false, false, ["SessionStop: fixture denied"]) { ControlDenied = true };
        metadata.Dispose();
        Assert.Equal("Idle", metadata.GetSnapshot().State);
        Assert.True(network.GetSnapshot().IsRunning);
        network.Dispose();
        Assert.False(owner.GetSnapshot().IsRunning);
        Assert.True(runtime.DisposalCompleted);
        Assert.Equal("StopFailed", owner.GetSnapshot().State);
        Assert.Equal("Idle", network.GetSnapshot().State);
        runtime.StopResult = KernelEtwSessionStopResult.Released;
        owner.Reconcile();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeniedUpdateKeepsExistingPublicationBindingsAndRetriesOnlyPendingKeywords(bool win32)
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        var received = 0;
        using var first = broker.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch,
            (source, _) => source.ObservePublications(() => received++)));
        var original = Assert.Single(factory.Calls).Runtime;
        var before = broker.GetSnapshot();
        original.UpdateFailure = win32 ? new System.ComponentModel.Win32Exception(5, "fixture access denied")
            : new UnauthorizedAccessException("fixture access denied");
        using var pending = broker.Acquire(new("disk", KernelTraceEventParser.Keywords.DiskIO, static (_, _) => { }));
        var sameKeywordReceived = 0;
        using var covered = broker.Acquire(new("cpu-2", KernelTraceEventParser.Keywords.ContextSwitch,
            (source, _) => source.ObservePublications(() => sameKeywordReceived++)));
        broker.Owner.Reconcile();
        original.EmitPublicationBoundary();

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LastReleaseStillStopsAndDisposesWhenSubscriptionUnregisterFails(bool accessDenied)
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        using var subscription = broker.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        var original = Assert.Single(factory.Calls).Runtime;
        original.UpdateFailure = accessDenied
            ? new UnauthorizedAccessException("fixture unregister denied")
            : new InvalidOperationException("fixture unregister failed");
        subscription.Dispose();
        Assert.Equal(1, original.StopCount);
        Assert.True(original.DisposalCompleted);
        Assert.False(original.IsProcessing);
        Assert.Equal("Idle", broker.GetSnapshot().State);
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
        using var replacement = broker.Acquire(new("next", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
        Assert.Equal(2, factory.Calls.Count);
        Assert.True(replacement.GetSnapshot().IsRunning);
    }

    [Fact]
    public void DeniedLastStopDisposesLocalConsumerAndBlocksNativeReplacement()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        var first = broker.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        var runtime = Assert.Single(factory.Calls).Runtime;
        runtime.StopResult = new(false, false, false, ["SessionStop: fixture denied"]) { ControlDenied = true };
        first.Dispose();
        broker.Owner.Reconcile();
        Assert.False(runtime.IsProcessing);
        Assert.True(runtime.DisposalCompleted);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        Assert.Equal(KernelTraceEventParser.Keywords.None, broker.GetSnapshot().EnabledKeywords);
        Assert.Contains("fixture denied", broker.GetSnapshot().Message);
        Assert.Equal(1, runtime.StopCount);
        using var second = broker.Acquire(new("cpu-2", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        broker.Owner.Reconcile();
        Assert.Single(factory.Calls);
        Assert.False(second.GetSnapshot().IsRunning);
    }

    [Fact]
    public void DiskConsumerReportsTheKeywordDenialWhileExistingNativeBindingContinues()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        var received = 0;
        using var cpu = broker.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch,
            (source, _) => source.ObservePublications(() => received++)));
        var runtime = Assert.Single(factory.Calls).Runtime;
        runtime.UpdateFailure = new System.ComponentModel.Win32Exception(5, "fixture disk control denied");
        using var disk = new ResourceManager.ReadWrite.Windows.Processes.EtwPhysicalDiskIoCaptureOwner(broker);

        disk.Start();
        var denied = disk.Read();
        Assert.Equal(ResourceManager.App.Application.ResourceBreakdown.PhysicalDiskIoProviderStates.Unavailable, denied.ProviderState.State);
        Assert.Contains("fixture disk control denied", denied.ProviderState.Message);
        Assert.False(disk.IsRunning);
        runtime.EmitPublicationBoundary();
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
        using var cpu = broker.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch,
            (source, _) => source.ObservePublications(() => received++)));
        var runtime = Assert.Single(factory.Calls).Runtime;
        runtime.UpdateFailure = new InvalidOperationException("fixture update failure requires release");
        runtime.StopResult = new(false, false, false, ["SessionStop: fixture access denied"])
        { ControlDenied = true };
        using var disk = broker.Acquire(new("disk", KernelTraceEventParser.Keywords.DiskIO, static (_, _) => { }));
        runtime.EmitPublicationBoundary();
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
        using var first = broker.Acquire(new("process", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
        var original = Assert.Single(factory.Calls).Runtime;
        original.UpdateFailure = new InvalidOperationException("fixture subscription update failure");

        using var second = broker.Acquire(new("network", KernelTraceEventParser.Keywords.NetworkTCPIP, static (_, _) => { }));

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
        using var first = broker.Acquire(new("process", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
        var original = Assert.Single(factory.Calls).Runtime;
        original.UpdateFailure = new InvalidOperationException("fixture subscription update failure");

        using var second = broker.Acquire(new("network", KernelTraceEventParser.Keywords.NetworkTCPIP, static (_, _) => { }));
        using var third = broker.Acquire(new("files", KernelTraceEventParser.Keywords.FileIO, static (_, _) => { }));

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

        var cpu = broker.Acquire(new KernelEtwSubscriptionRequest(
            "cpu",
            KernelTraceEventParser.Keywords.ContextSwitch,
            static (_, _) => { }));

        Assert.Single(factory.Calls);
        Assert.Equal(KernelTraceEventParser.Keywords.ContextSwitch, factory.Calls[0].Keywords);
        Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(1, broker.GetSnapshot().Generation);

        var network = broker.Acquire(new KernelEtwSubscriptionRequest(
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
        var subscription = broker.Acquire(new KernelEtwSubscriptionRequest(
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
    public async Task HeldResourceReconcilesProcessingFailureAndLastReleaseDrainsTheReplacement()
    {
        var factory = new FakeRuntimeFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromMilliseconds(10));
        using var subscription = broker.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        var first = Assert.Single(factory.Calls);
        first.Runtime.StopProcessingUnexpectedly();
        await WaitUntilAsync(() => factory.Calls.Count == 2 && broker.GetSnapshot().IsRunning);
        Assert.True(first.Runtime.DisposalCompleted);
        var replacement = factory.Calls[1];
        Assert.True(replacement.Context.Generation > first.Context.Generation);
        Assert.Equal(new[] { "cpu" }, replacement.SubscriptionIds);
        subscription.Dispose();
        Assert.True(replacement.Runtime.DisposalCompleted);
        Assert.False(replacement.Runtime.IsProcessing);
        Assert.Equal("Idle", broker.GetSnapshot().State);
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
        await Task.Delay(75);
        Assert.Equal(2, factory.Calls.Count);
        Assert.Equal(1, replacement.Runtime.StopCount);
    }

    [Fact]
    public async Task LastHolderReleaseWaitsForAnInFlightReconciliationBeforeDrainingItsRuntime()
    {
        using var startRelease = new ManualResetEventSlim();
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeRuntimeFactory
        {
            SecondRuntimeStartEntered = startEntered,
            SecondRuntimeStartRelease = startRelease
        };
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance,
            TimeSpan.FromMilliseconds(10));
        using var subscription = broker.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        Assert.Single(factory.Calls).Runtime.StopProcessingUnexpectedly();
        Task? release = null;
        try
        {
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // This action deliberately blocks behind the timer's reconciliation.
            // Give it its own thread so its entry signal does not depend on the
            // same worker pool occupied by the blocked reconciliation callbacks.
            release = Task.Factory.StartNew(() => { releaseEntered.SetResult(); subscription.Dispose(); },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(release.IsCompleted);
        }
        finally
        {
            startRelease.Set();
            if (release is not null) await release.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var replacement = factory.Calls[1].Runtime;
        Assert.True(replacement.DisposalCompleted);
        Assert.False(replacement.IsProcessing);
        Assert.Equal("Idle", broker.GetSnapshot().State);
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
        try
        {
            using var subscription = broker.Acquire(new KernelEtwSubscriptionRequest(
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

            using var network = broker.Acquire(new("network", KernelTraceEventParser.Keywords.NetworkTCPIP,
                static (_, _) => { }));
            Assert.Single(factory.Calls);
            Assert.Single(first.Runtime.Updates);
            Assert.Equal(1, broker.GetSnapshot().EventsLost);
            Assert.Equal(0, first.Runtime.StopCount);
        }
        finally
        {
            broker.Dispose();
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
        try
        {
            using var subscription = broker.Acquire(new KernelEtwSubscriptionRequest(
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
            broker.Dispose();
        }
    }

    [Fact]
    public void FailedRuntimeReleaseCannotBeReportedAsStopped()
    {
        var factory = new FakeRuntimeFactory
        {
            RuntimeStopResult = new(false, true, true, ["SessionStop: fixture failure."])
        };
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        using var subscription = broker.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        subscription.Dispose();
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        Assert.Contains("fixture failure", broker.GetSnapshot().Message);
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(1, Assert.Single(factory.Calls).Runtime.StopCount);
        Assert.True(factory.Calls[0].Runtime.DisposalCompleted);
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
        var firstSubscription = broker.Acquire(new KernelEtwSubscriptionRequest(
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

        var secondSubscription = broker.Acquire(new KernelEtwSubscriptionRequest(
            "process",
            KernelTraceEventParser.Keywords.Process,
            static (_, _) => { }));
        secondSubscription.Dispose();
        firstSubscription.Dispose();
        await Task.Delay(25);

        Assert.Single(factory.Calls);
        Assert.Equal(1, first.Runtime.StopCount);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        broker.Dispose();
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
        var firstSubscription = broker.Acquire(new KernelEtwSubscriptionRequest(
            "cpu",
            KernelTraceEventParser.Keywords.ContextSwitch,
            static (_, _) => { }));

        await WaitUntilAsync(() => broker.GetSnapshot().State == "StopFailed");
        await Task.Delay(75);
        var first = Assert.Single(factory.Calls);
        Assert.Equal(1, first.Runtime.StopCount);
        Assert.Contains("failed-start cleanup failure", broker.GetSnapshot().Message, StringComparison.Ordinal);

        var secondSubscription = broker.Acquire(new KernelEtwSubscriptionRequest(
            "thread",
            KernelTraceEventParser.Keywords.Thread,
            static (_, _) => { }));
        secondSubscription.Dispose();
        firstSubscription.Dispose();
        await Task.Delay(25);

        Assert.Single(factory.Calls);
        Assert.Equal(1, first.Runtime.StopCount);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        broker.Dispose();
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
        var firstSubscription = broker.Acquire(new KernelEtwSubscriptionRequest(
            "cpu",
            KernelTraceEventParser.Keywords.ContextSwitch,
            static (_, _) => { }));

        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        Assert.Equal(1, factory.CreateAttempts);
        await Task.Delay(75);
        Assert.Equal(1, factory.CreateAttempts);

        var secondSubscription = broker.Acquire(new KernelEtwSubscriptionRequest(
            "thread",
            KernelTraceEventParser.Keywords.Thread,
            static (_, _) => { }));
        secondSubscription.Dispose();
        firstSubscription.Dispose();
        await Task.Delay(25);

        Assert.Equal(1, factory.CreateAttempts);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        broker.Dispose();
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
    }

    private sealed class CleanupNativeControl : IKernelEtwNativeSessionControl
    {
        internal KernelEtwNativeSessionIdentity Identity { get; } = new(
            ResourceManagerEtwSessionNames.KernelSessionName, 1, Guid.NewGuid(),
            KernelEtwSessionKeywordControl.RealTimeMode | KernelEtwSessionKeywordControl.SystemLoggerMode);
        internal Exception? Failure { get; set; }
        internal int StopAttempts { get; private set; }
        public KernelEtwNativeSessionIdentity Query(string sessionName) => Identity;
        public void SetSystemTraceFlags(ulong sessionHandle, ulong flags) => throw new InvalidOperationException("No keywords expected.");
        public void StopSession(ulong sessionHandle, string sessionName)
        {
            StopAttempts++;
            if (Failure is not null) throw Failure;
        }
    }

    private sealed class FakeRuntimeFactory : IKernelEtwSessionRuntimeFactory
    {
        private readonly object gate = new();
        private readonly List<CreateCall> calls = [];
        private int createAttempts;
        private int availabilityChecks;
        public int AvailabilityChecks => Volatile.Read(ref availabilityChecks);
        public Exception? AvailabilityFailure { get; set; }
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
        public Exception? FirstRuntimeDisposalFailure { get; init; }
        public Exception? FirstCreateFailure { get; init; }
        public KernelEtwSessionStopResult RuntimeStopResult { get; init; } =
            KernelEtwSessionStopResult.Released;
        public ManualResetEventSlim? RuntimeDisposalRelease { get; init; }
        public TaskCompletionSource? SecondRuntimeStartEntered { get; init; }
        public ManualResetEventSlim? SecondRuntimeStartRelease { get; init; }

        public bool TryGetAvailability(out string state, out string message)
        {
            Interlocked.Increment(ref availabilityChecks);
            if (AvailabilityFailure is not null) throw AvailabilityFailure;
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
                    DisposalFailure = attempt == 1 ? FirstRuntimeDisposalFailure : null,
                    StopResult = RuntimeStopResult,
                    DisposalRelease = RuntimeDisposalRelease,
                    StartEntered = attempt == 2 ? SecondRuntimeStartEntered : null,
                    StartRelease = attempt == 2 ? SecondRuntimeStartRelease : null,
                };
                runtime.Initialize(subscriptions, context, keywords);
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
        private readonly NativeKernelEtwTestResource resource = new(NativeKernelEtwTestResource.SmallConfiguration(1000, 1));
        private IKernelEtwSessionRuntime? nativeRuntime;
        private readonly List<KernelEtwEventSource> sources = [];
        private long tick;
        internal void Initialize(IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions,
            KernelEtwSessionContext context, KernelTraceEventParser.Keywords keywords)
            => nativeRuntime = resource.RuntimeFactory.Create("broker-control-seam", keywords, context, Wrap(subscriptions));
        private KernelEtwSubscriptionRegistration[] Wrap(IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
            => subscriptions.Select(request => request with { NativeOutputs = request.NativeOutputs == KernelEtwNativeOutputs.None
                ? KernelEtwNativeOutputs.Residency : request.NativeOutputs,
                Bind = (source, context) => { sources.Add(source); request.Bind(source, context); } }).ToArray();
        public void EmitPublicationBoundary() => resource.Publish(++tick);
        private void CancelBindings() { foreach (var source in sources) source.Cancel(); }
        public Exception? UpdateFailure { get; set; }
        public bool IsProcessing => Volatile.Read(ref isProcessing) != 0;
        public int EventsLost
        {
            get => Volatile.Read(ref eventsLost);
            set => Volatile.Write(ref eventsLost, value);
        }
        public int StopCount => Volatile.Read(ref stopCount);
        public Exception? StartFailure { get; init; }
        public TaskCompletionSource? StartEntered { get; init; }
        public ManualResetEventSlim? StartRelease { get; init; }
        public KernelEtwSessionStopResult StopResult { get; set; } =
            KernelEtwSessionStopResult.Released;
        public ManualResetEventSlim? DisposalRelease { get; init; }
        public TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DisposalCompleted { get; private set; }
        public Exception? DisposalFailure { get; set; }
        public int DisposalAttempts { get; private set; }

        public void UpdateSubscriptions(
            IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions,
            KernelEtwSessionContext context)
        {
            Assert.True(IsProcessing);
            resource.KeywordUpdateFailure = UpdateFailure;
            nativeRuntime!.UpdateSubscriptions(Wrap(subscriptions), context);
            Updates.Add(subscriptions.Aggregate(KernelTraceEventParser.Keywords.None,
                static (all, subscription) => all | subscription.Keywords));
        }

        public void StartProcessing(Action<Exception?> completed)
        {
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            nativeRuntime!.StartProcessing(static _ => { });
            Volatile.Write(ref isProcessing, 1);
            StartEntered?.TrySetResult();
            StartRelease?.Wait();
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
            CancelBindings();
            Volatile.Write(ref isProcessing, 0);
            return StopResult;
        }

        public void Dispose()
        {
            DisposalAttempts++;
            DisposalEntered.TrySetResult();
            if (DisposalRelease is not null)
            {
                DisposalRelease.Wait();
            }
            CancelBindings();
            Volatile.Write(ref isProcessing, 0);
            if (DisposalFailure is not null) throw DisposalFailure;
            nativeRuntime!.Dispose();
            resource.Dispose();
            DisposalCompleted = true;
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
