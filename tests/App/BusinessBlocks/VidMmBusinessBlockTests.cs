using Microsoft.Extensions.Hosting;
using System.Reflection;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class VidMmBusinessBlockTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly int BlockId = BackendUids.DxgkrnlVidMmEtwTelemetryZone;

    [Fact]
    public async Task CallbackFailureFinishesReportingBeforePublishingItsFinalRunResult()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, true).WaitAsync(TestTimeout);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Running);
        var session = harness.Sessions.Current;
        await session.ProcessEntered.WaitAsync(TestTimeout);
        using var releasePublication = new ManualResetEventSlim();
        var publicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reader = harness.Fixture.Authority.Subscribe(BackendItems.VidMmNativeSessionAvailable,
            "vidmm-held-failure-publication", _ =>
            {
                if (!harness.Fixture.Authority.Read<bool>(BackendItems.VidMmNativeSessionAvailable).Value)
                {
                    publicationEntered.TrySetResult();
                    if (!releasePublication.Wait(TestTimeout))
                        throw new TimeoutException("The test did not release the native failure publication.");
                }
                return Task.CompletedTask;
            });
        var error = new UnauthorizedAccessException("injected callback permission failure");
        var signalFailure = typeof(DxgkrnlVidMmEtwTelemetryZone)
            .GetMethod("SignalFailure", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var resultTask = harness.NextResultAsync();
        var reporting = Task.Run(() => signalFailure.Invoke(harness.Zone, [error]));
        try
        {
            await publicationEntered.Task.WaitAsync(TestTimeout);
            await session.ProcessExited.WaitAsync(TestTimeout);
            // Cleanup has run, but the callback still owns an unfinished failure publication.
            await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.False(resultTask.IsCompleted);
        }
        finally
        {
            releasePublication.Set();
            await reporting.WaitAsync(TestTimeout);
        }
        var result = await resultTask;
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Run, result.FailureStage);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, result.FailureType);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
        Assert.NotNull(harness.Availability.ReadFailure(BlockId));
        Assert.False(harness.Fixture.Authority.Read<bool>(BackendItems.VidMmNativeSessionAvailable).Value);
        Assert.Equal(1, session.DisposeCount);
        Assert.False(session.ConsumerThread!.IsAlive);
        Assert.Empty(harness.Diagnostics.Read(BlockId).Metrics.ActiveThreads);

        signalFailure.Invoke(harness.Zone, [new IOException("late callback after run completion")]);
        Assert.Equal(result, harness.Diagnostics.Read(BlockId).Result);
        Assert.Equal(1, harness.Sessions.Attempts);
        AssertFieldInUse(harness, true);
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, false).WaitAsync(TestTimeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningPermissionFailureRemainsUnavailableAfterConfirmedCleanup(bool signalFailure)
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, true).WaitAsync(TestTimeout);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Running);
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
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
        Assert.NotNull(harness.Availability.ReadFailure(BackendUids.DxgkrnlVidMmEtwTelemetryZone));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.Equal(1, session.DisposeCount);
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.False(session.ConsumerThread!.IsAlive);
        Assert.False(Assert.IsType<bool>(Assert.Single(harness.Controller.Read().Items,
            fact => fact.ItemUid == BackendItems.VidMmNativeSessionAvailable).Value));
        Assert.Empty(harness.Diagnostics.Read(BlockId).Metrics.ActiveThreads);
        AssertFieldInUse(harness, true);
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, false).WaitAsync(TestTimeout);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreleasedRuntimePublishesUnavailableAndParentDoesNotRestartActiveField(bool creationCleanupFailure)
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        if (creationCleanupFailure)
            harness.Sessions.BeforeCreate = () => throw new KernelEtwSessionCreationCleanupException(
                new IOException("create failed"), new IOException("cleanup failed"));
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, true).WaitAsync(TestTimeout);
        if (!creationCleanupFailure)
        {
            await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Running);
            var session = harness.Sessions.Current;
            await session.ProcessEntered.WaitAsync(TestTimeout);
            session.BeforeDispose = () => throw new IOException("disposal failed");
            session.Complete();
        }
        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
        Assert.NotNull(harness.Availability.ReadFailure(BackendUids.DxgkrnlVidMmEtwTelemetryZone));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.False(Assert.IsType<bool>(Assert.Single(harness.Controller.Read().Items,
            fact => fact.ItemUid == BackendItems.VidMmNativeSessionAvailable).Value));
        AssertFieldInUse(harness, true);
        Assert.Empty(harness.Diagnostics.Read(BlockId).Metrics.ActiveThreads);
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, false).WaitAsync(TestTimeout);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
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
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, true).WaitAsync(TestTimeout);
        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
        Assert.NotNull(harness.Availability.ReadFailure(BackendUids.DxgkrnlVidMmEtwTelemetryZone));
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, true).WaitAsync(TestTimeout);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.Equal("Stopped", harness.Zone.State);
        Assert.Contains(harness.ControllerLogger.Entries, entry => entry.Exception == error);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, harness.Diagnostics.Read(BlockId).Result?.FailureType);
        Assert.False(Assert.IsType<bool>(Assert.Single(harness.Controller.Read().Items,
            fact => fact.ItemUid == BackendItems.VidMmNativeSessionAvailable).Value));
        if (afterCreation) Assert.Equal(1, harness.Sessions.Current.DisposeCount);
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, false).WaitAsync(TestTimeout);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
    }

    [Fact]
    public async Task DecisionDispatchDoesNotWaitForNativeStartupAndStoppedWaitsForNativeDrain()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        using var startGate = new ManualResetEventSlim();
        using var stopGate = new ManualResetEventSlim();
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Sessions.BeforeCreate = () => { creating.TrySetResult(); startGate.Wait(TestTimeout); };
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, true).WaitAsync(TestTimeout);
        try
        {
            await creating.Task.WaitAsync(TestTimeout);
            await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, true).WaitAsync(TestTimeout);
            Assert.Equal("Starting", harness.Zone.State);
            var ready = harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Running);
            Assert.False(ready.IsCompleted);
            startGate.Set();
            await ready.WaitAsync(TestTimeout);
            Assert.Equal("Running", harness.Zone.State);
            var session = harness.Sessions.Current;
            await session.ProcessEntered.WaitAsync(TestTimeout);
            session.BeforeDispose = () => { disposing.TrySetResult(); stopGate.Wait(TestTimeout); };
            await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, false).WaitAsync(TestTimeout);
            var released = harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
            await disposing.Task.WaitAsync(TestTimeout);
            Assert.Equal("Stopping", harness.Zone.State);
            Assert.False(released.IsCompleted);
            Assert.True(session.ConsumerThread!.IsAlive);
            stopGate.Set();
            await released.WaitAsync(TestTimeout);
            await harness.NextResultAsync();
            await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
            Assert.False(session.ConsumerThread.IsAlive);
        }
        finally
        {
            startGate.Set();
            stopGate.Set();
            await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, false).WaitAsync(TestTimeout);
            await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
        }
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
    public async Task RepeatedActiveFieldUsesOneRunAndWithdrawalJoinsTheConsumerThread()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, true).WaitAsync(TestTimeout);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Running);
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
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, true).WaitAsync(TestTimeout);
        Assert.Equal(1, harness.Sessions.Attempts);
        var thread = Assert.Single(harness.Diagnostics.Read(BlockId).Metrics.ActiveThreads);
        Assert.True(thread.NativeThreadId > 0);
        Assert.Equal(session.SessionName, thread.SessionName);
        AssertFieldInUse(harness, true);
        Assert.Equal("Running", harness.Zone.State);
        Assert.Equal(0, session.DisposeCount);
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, false).WaitAsync(TestTimeout);
        Assert.Equal(BusinessBlockOutcome.Stopped, (await harness.NextResultAsync()).Outcome);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
        Assert.Equal(1, session.DisposeCount);
        Assert.NotSame(session.ConsumerThread, session.DisposeThread);
        Assert.True(session.ConsumerWasAliveAtDispose);
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.False(session.ConsumerThread!.IsAlive);
        Assert.True(registration.IsDisposed);
        Assert.Equal(evidence, Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45))));
        Assert.Empty(harness.Diagnostics.Read(BlockId).Metrics.ActiveThreads);
        AssertFieldInUse(harness, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionReturnRecordsItsOwnResultAndParentRestartsActiveField(bool fail)
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, true).WaitAsync(TestTimeout);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Running);
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
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Running);
        Assert.Equal("Running", harness.Zone.State);
        if (fail) Assert.Empty(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));
        else Assert.Equal(evidence, Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45))));
        AssertFieldInUse(harness, true);
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, false).WaitAsync(TestTimeout);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
    }

    [Fact]
    public async Task ActiveFieldDoesNotExpireWhenThereAreNoEvidenceReads()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, true).WaitAsync(TestTimeout);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Running);
        var session = harness.Sessions.Current;
        await session.ProcessEntered.WaitAsync(TestTimeout);
        await Task.Delay(TimeSpan.FromSeconds(6.25));
        Assert.Equal("Running", harness.Zone.State);
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.Equal(0, session.DisposeCount);
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, false).WaitAsync(TestTimeout);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
        Assert.Equal(BusinessBlockOutcome.Stopped, (await harness.NextResultAsync()).Outcome);
    }

    [Fact]
    public async Task SubscriptionWithdrawalRetainsEvidenceWhileNativeResourcesAreReleased()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, true).WaitAsync(TestTimeout);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Running);
        var firstSession = harness.Sessions.Current;
        await firstSession.ProcessEntered.WaitAsync(TestTimeout);
        var now = DateTimeOffset.UtcNow;
        harness.Zone.ObserveProcessMemory(41, "game.exe", 32 * 1024 * 1024,
            "VidMmProcessUsageChange", checked((ulong)now.UtcDateTime.ToFileTimeUtc()), now);
        var evidence = Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BlockId, false).WaitAsync(TestTimeout);
        await harness.Fixture.WaitForStateAsync(BlockId, BusinessBlockState.Stopped);
        Assert.Equal(BusinessBlockOutcome.Stopped, (await harness.NextResultAsync()).Outcome);
        Assert.Equal(evidence, Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45))));
        Assert.Equal(1, firstSession.DisposeCount);
        Assert.False(firstSession.ConsumerThread!.IsAlive);
        AssertFieldInUse(harness, false);
    }

    private static void AssertFieldInUse(VidMmBusinessBlockTestHarness harness, bool expected)
        => Assert.Equal(expected, Assert.IsType<bool>(Assert.Single(harness.Controller.Read().Items,
            item => item.ItemUid == BackendItems.VramResidualInUse).Value));
}
