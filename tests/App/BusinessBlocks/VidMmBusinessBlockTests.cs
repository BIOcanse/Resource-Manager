using Microsoft.Extensions.Hosting;
using System.Reflection;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class VidMmBusinessBlockTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly int BlockId = BackendUids.DxgkrnlVidMmEtwTelemetryZone;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningPermissionFailureRemainsUnavailableAfterConfirmedCleanup(bool signalFailure)
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var subscription = harness.Zone.AcquireSubscription("running-permission-test", TimeSpan.FromSeconds(1));
        await subscription.Ready.WaitAsync(TestTimeout);
        var session = harness.Sessions.Current;
        await session.ProcessEntered.WaitAsync(TestTimeout);
        var error = new UnauthorizedAccessException("injected running native permission failure");
        if (signalFailure)
            typeof(DxgkrnlVidMmEtwTelemetryZone).GetMethod("SignalFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(harness.Zone, [error]);
        else session.Complete(error);
        var result = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Run, result.FailureStage);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, result.FailureType);
        await EtwTestRunRecorder.UntilAsync(() => harness.Zone.State == "Stopped");
        await harness.Zone.PublishAvailabilityAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.Equal(1, session.DisposeCount);
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.False(session.ConsumerThread!.IsAlive);
        Assert.False(Assert.Single(harness.Controller.Read().Facts,
            fact => fact.Key == DxgkrnlVidMmEtwTelemetryZone.AvailabilityFact).Value);
        Assert.Empty(harness.Diagnostics.Read(BlockId).Metrics.ActiveThreads);
        Assert.Single(harness.Controller.Read().Demands);
        await EtwTestRunRecorder.ReleaseAsync(subscription);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreleasedRuntimePublishesUnavailableAndParentDoesNotRestartHeldDemand(bool creationCleanupFailure)
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        if (creationCleanupFailure)
            harness.Sessions.BeforeCreate = () => throw new KernelEtwSessionCreationCleanupException(
                new IOException("create failed"), new IOException("cleanup failed"));
        var subscription = harness.Zone.AcquireSubscription("unreleased-runtime", TimeSpan.FromSeconds(1));
        if (!creationCleanupFailure)
        {
            await subscription.Ready.WaitAsync(TestTimeout);
            var session = harness.Sessions.Current;
            await session.ProcessEntered.WaitAsync(TestTimeout);
            session.BeforeDispose = () => throw new IOException("disposal failed");
            session.Complete();
        }
        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        await EtwTestRunRecorder.UntilAsync(() => harness.Zone.State == "Stopped");
        await harness.Zone.PublishAvailabilityAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.False(Assert.Single(harness.Controller.Read().Facts,
            fact => fact.Key == DxgkrnlVidMmEtwTelemetryZone.AvailabilityFact).Value);
        Assert.Single(harness.Controller.Read().Demands);
        Assert.Empty(harness.Diagnostics.Read(BlockId).Metrics.ActiveThreads);
        await EtwTestRunRecorder.ReleaseAsync(subscription);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionFailurePublishesUnavailableAndReturnsWithoutRepeatedNativeAttempts(bool afterCreation)
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var error = new UnauthorizedAccessException("injected non-admin ETW startup");
        if (afterCreation) harness.Sessions.Configure = session => session.BindFailure = error;
        else harness.Sessions.BeforeCreate = () => throw error;
        var subscription = harness.Zone.AcquireSubscription("permission-test", TimeSpan.FromSeconds(1));
        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        await EtwTestRunRecorder.UntilAsync(() => harness.Zone.State == "Stopped");
        await harness.Zone.PublishAvailabilityAsync();
        var second = harness.Zone.AcquireSubscription("another-consumer", TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.Equal("Stopped", harness.Zone.State);
        Assert.Contains(harness.ControllerLogger.Entries, entry => entry.Exception == error);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, harness.Diagnostics.Read(BlockId).Result?.FailureType);
        Assert.False(Assert.Single(harness.Controller.Read().Facts,
            fact => fact.Key == DxgkrnlVidMmEtwTelemetryZone.AvailabilityFact).Value);
        if (afterCreation) Assert.Equal(1, harness.Sessions.Current.DisposeCount);
        await EtwTestRunRecorder.ReleaseAsync(subscription);
        await EtwTestRunRecorder.ReleaseAsync(second);
    }

    [Fact]
    public async Task DecisionDispatchDoesNotWaitForNativeStartupAndDomainReleaseWaitsForNativeDrain()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        using var startGate = new ManualResetEventSlim();
        using var stopGate = new ManualResetEventSlim();
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Sessions.BeforeCreate = () => { creating.TrySetResult(); startGate.Wait(TestTimeout); };
        var dependency = harness.Zone.AcquireSubscription("native-readiness", TimeSpan.FromSeconds(1));
        try
        {
            await creating.Task.WaitAsync(TestTimeout);
            await harness.Controller.SetEnabledAsync(BlockId, true,
                new(BusinessBlockRequestKind.Configuration, "test", "same-current-enable", 0)).WaitAsync(TestTimeout);
            Assert.Equal("Starting", harness.Zone.State);
            var ready = Task.Run(async () => await dependency.Ready);
            Assert.False(ready.IsCompleted);
            startGate.Set();
            await ready.WaitAsync(TestTimeout);
            Assert.Equal("Running", harness.Zone.State);
            var session = harness.Sessions.Current;
            await session.ProcessEntered.WaitAsync(TestTimeout);
            session.BeforeDispose = () => { disposing.TrySetResult(); stopGate.Wait(TestTimeout); };
            var released = dependency.DisposeAsync().AsTask();
            await disposing.Task.WaitAsync(TestTimeout);
            Assert.Equal("Stopping", harness.Zone.State);
            Assert.False(released.IsCompleted);
            Assert.True(session.ConsumerThread!.IsAlive);
            stopGate.Set();
            await released.WaitAsync(TestTimeout);
            await harness.NextResultAsync();
            await EtwTestRunRecorder.UntilAsync(() => harness.Zone.State == "Stopped");
            Assert.False(session.ConsumerThread.IsAlive);
        }
        finally { startGate.Set(); stopGate.Set(); await EtwTestRunRecorder.ReleaseAsync(dependency); }
    }

    [Fact]
    public async Task StateAndEvidenceReadsDoNotStartEtwOrRepeatProcessIdentityReads()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        Assert.Equal("Stopped", harness.Zone.State);
        Assert.Equal(string.Empty, harness.Zone.Message);
        var observedAt = DateTimeOffset.UtcNow;
        harness.Zone.ObserveProcessMemory(41, "game.exe", 64 * 1024 * 1024,
            "VidMmProcessUsageChange", checked((ulong)observedAt.UtcDateTime.ToFileTimeUtc()), observedAt);
        var identityReads = harness.ProcessMetadataOpenCount;
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal("Stopped", harness.Zone.State);
            _ = harness.Controller.Read();
            Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));
            Assert.Single(harness.Zone.ReadCurrentEvidence(observedAt, TimeSpan.FromSeconds(45)));
        }
        Assert.Equal(identityReads, harness.ProcessMetadataOpenCount);
        Assert.Equal(0, harness.Sessions.Attempts);
        Assert.False(typeof(BackgroundService).IsAssignableFrom(typeof(DxgkrnlVidMmEtwTelemetryZone)));
        Assert.False(typeof(IHostedService).IsAssignableFrom(typeof(DxgkrnlVidMmEtwTelemetryZone)));
    }

    [Fact]
    public async Task MultipleSubscriptionsShareOneRunAndLastWithdrawalJoinsTheConsumerThread()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var slow = harness.Zone.AcquireSubscription("residual-slow", TimeSpan.FromSeconds(30));
        await slow.Ready.WaitAsync(TestTimeout);
        var session = harness.Sessions.Current;
        await session.ProcessEntered.WaitAsync(TestTimeout);
        var registration = Assert.Single(harness.Observer.Registrations);
        Assert.Equal(BackendUids.DxgkrnlVidMmEtwTelemetryZone, registration.BlockId);
        Assert.Same(session.ConsumerThread, registration.Thread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, registration.Thread.ManagedThreadId);
        Assert.True(registration.Thread.Name?.Contains(BlockId.ToString(), StringComparison.Ordinal) == true);
        Assert.Equal("Running", harness.Zone.State);
        Assert.Equal(string.Empty, harness.Zone.Message);
        var now = DateTimeOffset.UtcNow;
        harness.Zone.ObserveProcessMemory(41, "game.exe", 64 * 1024 * 1024,
            "VidMmProcessUsageChange", checked((ulong)now.UtcDateTime.ToFileTimeUtc()), now);
        var evidence = Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));
        var fast = harness.Zone.AcquireSubscription("residual-fast", TimeSpan.FromSeconds(1));
        await fast.Ready.WaitAsync(TestTimeout);
        Assert.Equal(1, harness.Sessions.Attempts);
        var thread = Assert.Single(harness.Diagnostics.Read(BlockId).Metrics.ActiveThreads);
        Assert.True(thread.NativeThreadId > 0);
        Assert.Equal(session.SessionName, thread.SessionName);
        Assert.Equal(2, harness.Controller.Read().Demands.Count);
        Assert.Contains(harness.Controller.Read().Demands, demand => demand.Reason.Owner == "residual-slow"
            && demand.Reason.ConfigurationOrDependency == "gpu-vram-residual-subscription");
        await EtwTestRunRecorder.ReleaseAsync(slow);
        Assert.Equal("Running", harness.Zone.State);
        Assert.Equal(0, session.DisposeCount);
        await EtwTestRunRecorder.ReleaseAsync(fast);
        Assert.Equal(BusinessBlockOutcome.Stopped, (await harness.NextResultAsync()).Outcome);
        await EtwTestRunRecorder.UntilAsync(() => harness.Zone.State == "Stopped");
        Assert.Equal(1, session.DisposeCount);
        Assert.NotSame(session.ConsumerThread, session.DisposeThread);
        Assert.True(session.ConsumerWasAliveAtDispose);
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.False(session.ConsumerThread!.IsAlive);
        Assert.True(registration.IsDisposed);
        Assert.Equal(evidence, Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45))));
        Assert.Empty(harness.Diagnostics.Read(BlockId).Metrics.ActiveThreads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionReturnRecordsItsOwnResultAndParentRestartsHeldDemand(bool fail)
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var dependency = harness.Zone.AcquireSubscription("residual-consumer", TimeSpan.FromSeconds(5));
        await dependency.Ready.WaitAsync(TestTimeout);
        var session = harness.Sessions.Current;
        await session.ProcessEntered.WaitAsync(TestTimeout);
        var error = fail ? new IOException("injected VidMm failure") : null;
        var now = DateTimeOffset.UtcNow;
        harness.Zone.ObserveProcessMemory(41, "game.exe", 32 * 1024 * 1024,
            "VidMmProcessUsageChange", checked((ulong)now.UtcDateTime.ToFileTimeUtc()), now);
        var evidence = Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));
        session.Complete(error);
        var result = await harness.NextResultAsync();
        Assert.Equal(fail ? BusinessBlockOutcome.Failed : BusinessBlockOutcome.Done, result.Outcome);
        if (fail)
        {
            Assert.Equal(typeof(IOException).FullName, result.FailureType);
            Assert.Equal(BusinessBlockFailureStage.Run, result.FailureStage);
            Assert.Contains(harness.Observer.Failures, failure => failure.Error == error);
            Assert.Contains(harness.ControllerLogger.Entries, entry => entry.Exception == error);
        }
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.False(session.ConsumerThread!.IsAlive);
        Assert.Equal(1, session.DisposeCount);
        await EtwTestRunRecorder.UntilAsync(() => harness.Sessions.Attempts == 2);
        await dependency.Ready.WaitAsync(TestTimeout);
        Assert.Equal("Running", harness.Zone.State);
        if (fail) Assert.Empty(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));
        else Assert.Equal(evidence, Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45))));
        Assert.Single(harness.Controller.Read().Demands);
        Assert.False(dependency.Released.IsCompleted);
        await EtwTestRunRecorder.ReleaseAsync(dependency);
    }

    [Fact]
    public async Task ActiveDemandDoesNotExpireWhenThereAreNoEvidenceReads()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var dependency = harness.Zone.AcquireSubscription("idle-consumer", TimeSpan.FromSeconds(30));
        await dependency.Ready.WaitAsync(TestTimeout);
        var session = harness.Sessions.Current;
        await session.ProcessEntered.WaitAsync(TestTimeout);
        await Task.Delay(TimeSpan.FromSeconds(6.25));
        Assert.Equal("Running", harness.Zone.State);
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.Equal(0, session.DisposeCount);
        await EtwTestRunRecorder.ReleaseAsync(dependency);
        Assert.Equal(BusinessBlockOutcome.Stopped, (await harness.NextResultAsync()).Outcome);
    }

    [Fact]
    public async Task FreezeAndThawRetainEvidenceWhileNativeResourcesAreReleased()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var dependency = harness.Zone.AcquireSubscription("residual-consumer", TimeSpan.FromSeconds(30));
        await dependency.Ready.WaitAsync(TestTimeout);
        var firstSession = harness.Sessions.Current;
        await firstSession.ProcessEntered.WaitAsync(TestTimeout);
        var now = DateTimeOffset.UtcNow;
        harness.Zone.ObserveProcessMemory(41, "game.exe", 32 * 1024 * 1024,
            "VidMmProcessUsageChange", checked((ulong)now.UtcDateTime.ToFileTimeUtc()), now);
        var evidence = Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));
        await harness.Zone.ApplyModeAsync(ResourceManagerComputeZoneMode.Freeze).WaitAsync(TestTimeout);
        Assert.Equal(BusinessBlockOutcome.Stopped, (await harness.NextResultAsync()).Outcome);
        Assert.Equal(evidence, Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45))));
        Assert.Equal(1, firstSession.DisposeCount);
        Assert.False(firstSession.ConsumerThread!.IsAlive);
        await harness.Zone.ApplyModeAsync(ResourceManagerComputeZoneMode.Normal).WaitAsync(TestTimeout);
        await dependency.Ready.WaitAsync(TestTimeout);
        Assert.Equal(2, harness.Sessions.Attempts);
        Assert.Equal(evidence, Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45))));
        Assert.Equal("Running", harness.Zone.State);
        await EtwTestRunRecorder.ReleaseAsync(dependency);
    }
}
