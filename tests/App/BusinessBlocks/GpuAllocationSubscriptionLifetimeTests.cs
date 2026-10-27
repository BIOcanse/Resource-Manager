using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationSubscriptionLifetimeTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("enable")]
    [InlineData("capture")]
    [InlineData("disable")]
    [InlineData("consumer")]
    public async Task CaptureWithRetainedSubscriptionAfterPermissionFailureReturnsEmptyWithoutRestarting(string operation)
    {
        var denial = new UnauthorizedAccessException("injected GPU " + operation + " permission failure");
        var factory = new GpuAllocationTestSessionFactory();
        if (operation == "create") factory.CreateFailure = denial;
        await using var harness = factory.Harness();
        var subscription = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        if (operation != "create")
        {
            await EtwTestRunRecorder.ReadyAsync(subscription);
            await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
            Assert.NotNull(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
            var session = factory.Current;
            if (operation == "consumer") session.End(denial);
            else
            {
                if (operation == "enable") session.EnableFailure = denial;
                else if (operation == "capture") session.CaptureFailure = denial;
                else session.DisableFailure = denial;
                await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
            }
        }

        var result = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(BackendUids.GpuAllocationCollector)
            == BusinessBlockState.Stopped);
        Assert.NotNull(harness.Availability.ReadFailure(BackendUids.GpuAllocationCollector));
        Assert.False(Assert.IsType<bool>(Assert.Single(harness.Controller.Read().Items,
            fact => fact.ItemUid == BackendItems.GpuAllocationNativeSessionAvailable).Value));
        Assert.Single(harness.Controller.Read().Demands);
        Assert.False(subscription.Released.IsCompleted);
        var captures = factory.Sessions.Sum(session => session.Captures);

        using var callerStop = new CancellationTokenSource();
        var capture = harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, callerStop.Token);
        try
        {
            await capture.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
            await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, callerStop.Token)
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
            Assert.Equal(1, factory.Attempts);
            Assert.Equal(captures, factory.Sessions.Sum(session => session.Captures));
            Assert.Equal(BusinessBlockState.Stopped, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
            Assert.Single(harness.Controller.Read().Demands);
        }
        finally
        {
            callerStop.Cancel();
            try { await capture; }
            catch (OperationCanceledException) when (callerStop.IsCancellationRequested) { }
            await EtwTestRunRecorder.ReleaseAsync(subscription);
        }
    }

    [Fact]
    public async Task WithdrawingOneSubscriptionDoesNotCancelAnotherSubscriptionsReadinessOrCapture()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var first = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        var second = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await EtwTestRunRecorder.ReadyAsync(second);
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.NotNull(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await harness.Gate.PublishAsync(false,
            new(BusinessBlockRequestKind.Configuration, "test", "pause-native-producer", 0));
        await harness.NextResultAsync();
        using var callerStop = new CancellationTokenSource();
        var capture = harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, callerStop.Token);
        var remainingReady = second.Ready;
        try
        {
            await capture.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
            await EtwTestRunRecorder.ReleaseAsync(first);
            Assert.False(remainingReady.IsCompleted);
            // A capture can have copied this lease before Dispose disposed its source.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.Ready);
            await harness.Gate.PublishAsync(true,
                new(BusinessBlockRequestKind.Configuration, "test", "resume-native-producer", 0));
            await remainingReady.WaitAsync(TimeSpan.FromSeconds(10));
            await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, callerStop.Token);
            Assert.Equal(2, factory.Attempts);
            Assert.Equal(1, factory.Current.Captures);
            Assert.NotNull(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        }
        finally
        {
            callerStop.Cancel();
            try { await capture; }
            catch (OperationCanceledException) when (callerStop.IsCancellationRequested) { }
            await EtwTestRunRecorder.ReleaseAsync(second);
        }
    }

    [Fact]
    public async Task CaptureWithRetainedSubscriptionWhileTheNativeRunIsStoppingReturnsEmptyWithoutWaitingForJoin()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var subscription = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await EtwTestRunRecorder.ReadyAsync(subscription);
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.NotNull(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        var session = factory.Current;
        using var joinGate = new ManualResetEventSlim();
        using var callerStop = new CancellationTokenSource();
        var joining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.BeforeJoin = () => { joining.TrySetResult(); Assert.True(joinGate.Wait(TimeSpan.FromSeconds(10))); };
        Task? capture = null;
        try
        {
            await harness.Gate.PublishAsync(false,
                new(BusinessBlockRequestKind.Configuration, "test", "pause-native-producer", 0));
            await joining.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(BusinessBlockState.Stopping, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
            Assert.False(session.Joined);

            capture = harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, callerStop.Token);
            await capture.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
            Assert.Equal(1, factory.Attempts);
            Assert.Equal(1, session.Captures);
            Assert.False(session.Joined);
            Assert.Single(harness.Controller.Read().Demands);
        }
        finally
        {
            callerStop.Cancel();
            joinGate.Set();
            if (capture is not null)
            {
                try { await capture; }
                catch (OperationCanceledException) when (callerStop.IsCancellationRequested) { }
            }
            await EtwTestRunRecorder.ReleaseAsync(subscription);
        }
        Assert.True(session.Joined);
    }

    [Fact]
    public async Task LastSubscriptionWithdrawalCancelsReadinessWithoutBlockingCaptureWhenTheRunIsAbsent()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        var subscription = harness.Collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await EtwTestRunRecorder.ReadyAsync(subscription);
        await harness.Gate.PublishAsync(false,
            new(BusinessBlockRequestKind.Configuration, "test", "pause-native-producer", 0));
        await harness.NextResultAsync();

        using var captureStop = new CancellationTokenSource();
        var ready = subscription.Ready;
        var capture = harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, captureStop.Token);
        try
        {
            await capture.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(ready.IsCompleted);
            await EtwTestRunRecorder.ReleaseAsync(subscription);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);
            Assert.Equal(1, factory.Attempts);
            Assert.Equal(0, factory.Current.Captures);
            Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        }
        finally
        {
            captureStop.Cancel();
            try { await capture; }
            catch (OperationCanceledException) when (captureStop.IsCancellationRequested) { }
        }
    }
}
