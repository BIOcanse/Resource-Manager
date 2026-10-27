using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationSessionLifetimeTests
{
    [Fact]
    public async Task ConsumerPermissionDenialBeforeStartProcessingReturnsCannotBeOverwrittenByStartup()
    {
        var factory = new GpuAllocationTestSessionFactory
        {
            Configure = session => session.ProcessingFailureOnStart = new UnauthorizedAccessException("injected immediate consumer denial")
        };
        await using var harness = factory.Harness();
        var subscription = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        var result = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, result.FailureType);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(BackendUids.GpuAllocationCollector) == BusinessBlockState.Stopped);
        await harness.Collector.PublishAvailabilityAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, factory.Current.Stops);
        Assert.True(factory.Current.StopResult.IsReleased);
        Assert.True(factory.Current.Joined);
        Assert.False(factory.Current.HasBoundHandler);
        Assert.False(Assert.Single(harness.Controller.Read().Facts, fact => fact.Key == GpuAllocationCollector.AvailabilityFact).Value);
        Assert.Single(harness.Controller.Read().Demands);
        await EtwTestRunRecorder.ReleaseAsync(subscription);
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("capture")]
    [InlineData("disable")]
    [InlineData("consumer")]
    public async Task RunningPermissionFailureRemainsUnavailableAfterConfirmedCleanup(string operation)
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var subscription = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await EtwTestRunRecorder.ReadyAsync(subscription);
        var session = factory.Current;
        var error = new UnauthorizedAccessException("injected running " + operation + " permission failure");
        if (operation == "consumer") session.End(error);
        else
        {
            if (operation == "enable") session.EnableFailure = error;
            else if (operation == "capture") session.CaptureFailure = error;
            else session.DisableFailure = error;
            await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, CancellationToken.None);
        }
        var result = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Run, result.FailureStage);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, result.FailureType);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(BackendUids.GpuAllocationCollector) == BusinessBlockState.Stopped);
        await harness.Collector.PublishAvailabilityAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, session.Stops);
        Assert.True(session.StopResult.IsReleased);
        Assert.True(session.Joined);
        Assert.False(session.HasBoundHandler);
        Assert.False(Assert.Single(harness.Controller.Read().Facts, fact => fact.Key == GpuAllocationCollector.AvailabilityFact).Value);
        Assert.Single(harness.Controller.Read().Demands);
        Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await EtwTestRunRecorder.ReleaseAsync(subscription);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreleasedRuntimePublishesUnavailableAndDoesNotRestartHeldDemand(bool creationCleanupFailure)
    {
        var factory = new GpuAllocationTestSessionFactory { PartialCleanupFailure = creationCleanupFailure };
        await using var harness = factory.Harness();
        var subscription = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        if (!creationCleanupFailure)
        {
            await EtwTestRunRecorder.ReadyAsync(subscription);
            factory.Current.StopResult = new(true, false, true, ["injected unconfirmed release"]);
            factory.Current.End();
        }
        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(BackendUids.GpuAllocationCollector) == BusinessBlockState.Stopped);
        await harness.Collector.PublishAvailabilityAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, factory.Attempts);
        Assert.False(Assert.Single(harness.Controller.Read().Facts, fact => fact.Key == GpuAllocationCollector.AvailabilityFact).Value);
        Assert.Single(harness.Controller.Read().Demands);
        await EtwTestRunRecorder.ReleaseAsync(subscription);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionFailurePublishesUnavailableAndANewSubscriptionCannotInventRecovery(bool afterCreation)
    {
        var error = new UnauthorizedAccessException("injected non-admin startup");
        var factory = new GpuAllocationTestSessionFactory();
        if (afterCreation) factory.Configure = session => session.EnableFailure = error;
        else factory.CreateFailure = error;
        await using var harness = factory.Harness();
        var subscription = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(BackendUids.GpuAllocationCollector) == BusinessBlockState.Stopped);
        await harness.Collector.PublishAvailabilityAsync();
        var second = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(BusinessBlockState.Stopped, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
        Assert.False(Assert.Single(harness.Controller.Read().Facts, fact => fact.Key == GpuAllocationCollector.AvailabilityFact).Value);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName,
            harness.Diagnostics.Read(BackendUids.GpuAllocationCollector).Result?.FailureType);
        if (afterCreation)
        {
            Assert.Equal(1, factory.Current.Stops);
            Assert.True(factory.Current.StopResult.IsReleased);
            Assert.True(factory.Current.Joined);
            Assert.False(factory.Current.HasBoundHandler);
        }
        await EtwTestRunRecorder.ReleaseAsync(subscription);
        await EtwTestRunRecorder.ReleaseAsync(second);
    }

    [Fact]
    public async Task FailedStartupReturnsAndTheParentRetriesExistingDemand()
    {
        var factory = new GpuAllocationTestSessionFactory { Failures = 1 };
        await using var harness = factory.Harness();
        var subscription = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        var failure = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, failure.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Start, failure.FailureStage);
        await EtwTestRunRecorder.UntilAsync(() => factory.Attempts == 2);
        await EtwTestRunRecorder.ReadyAsync(subscription);
        Assert.Equal(0, factory.Current.Captures);
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.NotNull(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await EtwTestRunRecorder.ReleaseAsync(subscription);
    }

    [Fact]
    public async Task DeniedStopCannotReturnUntilTheOwnedProcessingHasJoined()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var subscription = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await EtwTestRunRecorder.ReadyAsync(subscription);
        var session = factory.Current;
        session.StopResult = new(false, false, false, ["native control denied"]) { ControlDenied = true };
        session.HoldProcessingAfterStop = true;
        using var joinGate = new ManualResetEventSlim();
        var joining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.BeforeJoin = () => { joining.TrySetResult(); Assert.True(joinGate.Wait(TimeSpan.FromSeconds(10))); };
        var release = subscription.DisposeAsync().AsTask();
        try
        {
            await joining.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(release.IsCompleted);
            Assert.True(session.IsProcessing);
            Assert.False(session.Joined);
            Assert.Equal(BusinessBlockState.Stopping, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
            Assert.False(Assert.Single(harness.Controller.Read().Facts,
                fact => fact.Key == GpuAllocationCollector.AvailabilityFact).Value);
        }
        finally { session.End(); joinGate.Set(); }
        await release.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.Joined);
        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(BackendUids.GpuAllocationCollector) == BusinessBlockState.Stopped);
    }

    [Fact]
    public async Task UnexpectedExitReleasesTheOldSessionAndLateCallbacksCannotAffectItsReplacement()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var collector = harness.Collector;
        var subscription = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await EtwTestRunRecorder.ReadyAsync(subscription);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var old = factory.Current;
        old.End(new IOException("injected consumer failure"));
        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        Assert.Equal(1, old.Stops);
        Assert.Equal(Timeout.InfiniteTimeSpan, old.StopTimeout);
        await EtwTestRunRecorder.UntilAsync(() => factory.Attempts == 2);
        await EtwTestRunRecorder.ReadyAsync(subscription);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var current = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        Assert.NotNull(current);
        old.End(new IOException("late callback"));
        Assert.Same(current, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await EtwTestRunRecorder.ReleaseAsync(subscription);
        Assert.Equal(1, factory.Current.Stops);
    }

    [Fact]
    public async Task UnconfirmedSessionReleaseRetainsTheHandleAndBlocksReplacementSessions()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var first = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        await EtwTestRunRecorder.ReadyAsync(first);
        var session = factory.Current;
        session.StopResult = new(true, false, true, ["fake disposal failure"]);
        session.End();
        var failure = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, failure.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Stop, failure.FailureStage);
        await EtwTestRunRecorder.ReleaseAsync(first);
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, session.Stops);
        Assert.NotEmpty(session.Enabled);

        var next = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        var ready = next.Ready;
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(ready.IsCompleted);
        Assert.Equal(BusinessBlockState.Stopped, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
        Assert.False(Assert.Single(harness.Controller.Read().Facts, fact => fact.Key == GpuAllocationCollector.AvailabilityFact).Value);
        await EtwTestRunRecorder.ReleaseAsync(next);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, session.Stops);
    }

    [Fact]
    public async Task PartialFactoryCleanupFailureBlocksASecondNativeSession()
    {
        var factory = new GpuAllocationTestSessionFactory { PartialCleanupFailure = true };
        await using var harness = factory.Harness();
        var first = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        var failure = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, failure.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Start, failure.FailureStage);
        await EtwTestRunRecorder.ReleaseAsync(first);
        var next = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        var ready = next.Ready;
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(ready.IsCompleted);
        Assert.Equal(BusinessBlockState.Stopped, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
        await EtwTestRunRecorder.ReleaseAsync(next);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);
        Assert.Equal(1, factory.Attempts);
        Assert.Empty(factory.Sessions);
    }
}
