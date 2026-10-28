using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationSessionLifetimeTests
{
    [Fact]
    public async Task SessionJoinWaitsForAnInFlightCompletionCallback()
    {
        var session = new GpuAllocationTestSession("callback-drain");
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joinEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StartProcessing(_ =>
        {
            callbackEntered.TrySetResult();
            Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(5)));
        });
        session.BeforeJoin = () => joinEntered.TrySetResult();
        var completing = Task.Run(() => session.End(new UnauthorizedAccessException("completion")));
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var joining = Task.Run(session.JoinProcessing);
        try
        {
            await joinEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => joining.WaitAsync(TimeSpan.FromMilliseconds(50)));
            Assert.False(session.Joined);
        }
        finally { releaseCallback.Set(); }
        await Task.WhenAll(completing, joining).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Joined);
    }

    [Fact]
    public async Task ConsumerPermissionDenialBeforeStartProcessingReturnsCannotBeOverwrittenByStartup()
    {
        var factory = new GpuAllocationTestSessionFactory
        {
            Configure = session => session.ProcessingFailureOnStart = new UnauthorizedAccessException("injected immediate consumer denial")
        };
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        var result = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, result.FailureType);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(BackendUids.GpuAllocationCollector) == BusinessBlockState.Stopped);
        Assert.NotNull(harness.Availability.ReadFailure(BackendUids.GpuAllocationCollector));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, factory.Current.Stops);
        Assert.True(factory.Current.StopResult.IsReleased);
        Assert.True(factory.Current.Joined);
        Assert.False(factory.Current.HasBoundHandler);
        Assert.False(Assert.IsType<bool>(Assert.Single(harness.Controller.Read().Items, fact => fact.ItemUid == BackendItems.GpuAllocationNativeSessionAvailable).Value));
        Assert.True(harness.Fixture.Authority.Read<bool>(ResourceManager.App.StateItems.StateItemUids.ProcessDedicatedGpuMemoryInUse).Value);
        await harness.StopAsync();
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
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        var session = factory.Current;
        var error = new UnauthorizedAccessException("injected running " + operation + " permission failure");
        if (operation == "consumer") session.End(error);
        else
        {
            if (operation == "enable") session.EnableFailure = error;
            else if (operation == "capture") session.CaptureFailure = error;
            else session.DisableFailure = error;
            await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        var result = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Run, result.FailureStage);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, result.FailureType);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(BackendUids.GpuAllocationCollector) == BusinessBlockState.Stopped);
        Assert.NotNull(harness.Availability.ReadFailure(BackendUids.GpuAllocationCollector));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, session.Stops);
        Assert.True(session.StopResult.IsReleased);
        Assert.True(session.Joined);
        Assert.False(session.HasBoundHandler);
        Assert.False(Assert.IsType<bool>(Assert.Single(harness.Controller.Read().Items, fact => fact.ItemUid == BackendItems.GpuAllocationNativeSessionAvailable).Value));
        Assert.True(harness.Fixture.Authority.Read<bool>(ResourceManager.App.StateItems.StateItemUids.ProcessDedicatedGpuMemoryInUse).Value);
        Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await harness.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreleasedRuntimePublishesUnavailableAndDoesNotRestartWhileTheFieldIsInUse(bool creationCleanupFailure)
    {
        var factory = new GpuAllocationTestSessionFactory { PartialCleanupFailure = creationCleanupFailure };
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        if (!creationCleanupFailure)
        {
            await harness.WaitRunningAsync();
            factory.Current.StopResult = new(true, false, true, ["injected unconfirmed release"]);
            factory.Current.End();
        }
        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(BackendUids.GpuAllocationCollector) == BusinessBlockState.Stopped);
        Assert.NotNull(harness.Availability.ReadFailure(BackendUids.GpuAllocationCollector));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, factory.Attempts);
        Assert.False(Assert.IsType<bool>(Assert.Single(harness.Controller.Read().Items, fact => fact.ItemUid == BackendItems.GpuAllocationNativeSessionAvailable).Value));
        Assert.True(harness.Fixture.Authority.Read<bool>(ResourceManager.App.StateItems.StateItemUids.ProcessDedicatedGpuMemoryInUse).Value);
        await harness.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionFailurePublishesUnavailableAndRepeatedFieldNotificationsCannotInventRecovery(bool afterCreation)
    {
        var error = new UnauthorizedAccessException("injected non-admin startup");
        var factory = new GpuAllocationTestSessionFactory();
        if (afterCreation) factory.Configure = session => session.EnableFailure = error;
        else factory.CreateFailure = error;
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(BackendUids.GpuAllocationCollector) == BusinessBlockState.Stopped);
        Assert.NotNull(harness.Availability.ReadFailure(BackendUids.GpuAllocationCollector));
        await harness.SetInUseAsync(true);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(BusinessBlockState.Stopped, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
        Assert.False(Assert.IsType<bool>(Assert.Single(harness.Controller.Read().Items, fact => fact.ItemUid == BackendItems.GpuAllocationNativeSessionAvailable).Value));
        Assert.Equal(typeof(UnauthorizedAccessException).FullName,
            harness.Diagnostics.Read(BackendUids.GpuAllocationCollector).Result?.FailureType);
        if (afterCreation)
        {
            Assert.Equal(1, factory.Current.Stops);
            Assert.True(factory.Current.StopResult.IsReleased);
            Assert.True(factory.Current.Joined);
            Assert.False(factory.Current.HasBoundHandler);
        }
        await harness.StopAsync();
        await harness.StopAsync();
    }

    [Fact]
    public async Task FailedStartupReturnsAndTheParentRetriesWhileTheFieldIsInUse()
    {
        var factory = new GpuAllocationTestSessionFactory { Failures = 1 };
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        var failure = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, failure.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Start, failure.FailureStage);
        await EtwTestRunRecorder.UntilAsync(() => factory.Attempts == 2);
        await harness.WaitRunningAsync();
        Assert.Equal(0, factory.Current.Captures);
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.NotNull(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await harness.StopAsync();
    }

    [Fact]
    public async Task DeniedStopCannotReturnUntilTheOwnedProcessingHasJoined()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        var session = factory.Current;
        session.StopResult = new(false, false, false, ["native control denied"]) { ControlDenied = true };
        session.HoldProcessingAfterStop = true;
        using var joinGate = new ManualResetEventSlim();
        var joining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.BeforeJoin = () => { joining.TrySetResult(); Assert.True(joinGate.Wait(TimeSpan.FromSeconds(10))); };
        var release = harness.StopAsync();
        try
        {
            await joining.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(release.IsCompleted);
            Assert.True(session.IsProcessing);
            Assert.False(session.Joined);
            Assert.Equal(BusinessBlockState.Stopping, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
            Assert.False(Assert.IsType<bool>(Assert.Single(harness.Controller.Read().Items,
                fact => fact.ItemUid == BackendItems.GpuAllocationNativeSessionAvailable).Value));
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
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        var old = factory.Current;
        old.End(new IOException("injected consumer failure"));
        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        Assert.Equal(1, old.Stops);
        Assert.Equal(Timeout.InfiniteTimeSpan, old.StopTimeout);
        await EtwTestRunRecorder.UntilAsync(() => factory.Attempts == 2);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        var current = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        Assert.NotNull(current);
        old.End(new IOException("late callback"));
        Assert.Same(current, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await harness.StopAsync();
        Assert.Equal(1, factory.Current.Stops);
    }

    [Fact]
    public async Task UnconfirmedSessionReleaseKeepsAvailabilityFalseAndBlocksReplacementSessions()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        var session = factory.Current;
        session.StopResult = new(true, false, true, ["fake disposal failure"]);
        session.End();
        var failure = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, failure.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Stop, failure.FailureStage);
        await harness.StopAsync();
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, session.Stops);
        Assert.NotEmpty(session.Enabled);

        await harness.SetInUseAsync(true);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(BusinessBlockState.Stopped, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
        Assert.False(Assert.IsType<bool>(Assert.Single(harness.Controller.Read().Items, fact => fact.ItemUid == BackendItems.GpuAllocationNativeSessionAvailable).Value));
        await harness.StopAsync();
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, session.Stops);
    }

    [Fact]
    public async Task PartialFactoryCleanupFailureBlocksASecondNativeSession()
    {
        var factory = new GpuAllocationTestSessionFactory { PartialCleanupFailure = true };
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        var failure = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, failure.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Start, failure.FailureStage);
        await harness.StopAsync();
        await harness.SetInUseAsync(true);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(BusinessBlockState.Stopped, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
        await harness.StopAsync();
        Assert.Equal(1, factory.Attempts);
        Assert.Empty(factory.Sessions);
    }
}
