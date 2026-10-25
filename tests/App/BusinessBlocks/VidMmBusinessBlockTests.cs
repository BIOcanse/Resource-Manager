using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class VidMmBusinessBlockTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly string BlockId = nameof(DxgkrnlVidMmEtwTelemetryZone);

    [Fact]
    public async Task ControllerQueriesAndEvidenceReadsDoNotStartEtwOrRepeatProcessIdentityReads()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        Assert.NotNull(typeof(DxgkrnlVidMmEtwTelemetryZone).GetConstructor(
            [typeof(Microsoft.Extensions.Logging.ILogger<DxgkrnlVidMmEtwTelemetryZone>),
             typeof(ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockController),
             typeof(IEtwConsumerThreadObserver)]));
        Assert.Equal("Idle", harness.Zone.State);
        Assert.Equal(string.Empty, harness.Zone.Message);
        var observedAt = DateTimeOffset.UtcNow;
        harness.Zone.ObserveProcessMemory(41, "game.exe", 64 * 1024 * 1024,
            "VidMmProcessUsageChange", checked((ulong)observedAt.UtcDateTime.ToFileTimeUtc()), observedAt);
        var identityReadsAfterObservation = harness.ProcessMetadataOpenCount;

        for (var index = 0; index < 3; index++)
        {
            _ = harness.Controller.Read();
            Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));
            Assert.Single(harness.Zone.ReadCurrentEvidence(observedAt, TimeSpan.FromSeconds(45)));
        }

        Assert.Equal(identityReadsAfterObservation, harness.ProcessMetadataOpenCount);
        Assert.Equal(0, harness.Sessions.Attempts);
        Assert.Empty(harness.Sessions.Snapshot());
        Assert.False(typeof(BackgroundService).IsAssignableFrom(typeof(DxgkrnlVidMmEtwTelemetryZone)));
        Assert.False(typeof(IHostedService).IsAssignableFrom(typeof(DxgkrnlVidMmEtwTelemetryZone)));
    }

    [Fact]
    public async Task MultipleSubscriptionsShareOneRunAndTheLastWithdrawalJoinsTheConsumerThread()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var slow = harness.Zone.AcquireSubscription("residual-slow", TimeSpan.FromSeconds(30));
        await slow.Ready.WaitAsync(TestTimeout);
        var session = harness.Sessions.Current;
        await session.ProcessEntered.WaitAsync(TestTimeout);
        var firstRegistration = Assert.Single(harness.Observer.Registrations);

        Assert.Equal("vidmm-etw", firstRegistration.BlockId);
        Assert.Same(session.ConsumerThread, firstRegistration.Thread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, firstRegistration.Thread.ManagedThreadId);
        Assert.True(firstRegistration.Thread.Name?.Contains("vidmm-etw", StringComparison.Ordinal) == true);
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.Equal("Running", harness.Zone.State);
        Assert.Equal(string.Empty, harness.Zone.Message);

        var now = DateTimeOffset.UtcNow;
        harness.Zone.ObserveProcessMemory(41, "game.exe", 64 * 1024 * 1024,
            "VidMmProcessUsageChange", checked((ulong)now.UtcDateTime.ToFileTimeUtc()), now);
        var current = Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));

        var fast = harness.Zone.AcquireSubscription("residual-fast", TimeSpan.FromSeconds(1));
        await fast.Ready.WaitAsync(TestTimeout);
        Assert.Equal(1, harness.Sessions.Attempts);

        var block = Block(harness);
        var thread = Assert.Single(block.Metrics.ActiveThreads);
        Assert.True(thread.NativeThreadId > 0);
        Assert.Equal(session.SessionName, thread.SessionName);
        Assert.Equal(2, block.ActiveDependencies.Count);
        Assert.Contains(block.ActiveDependencies, dependency =>
            dependency.Owner == "residual-slow"
            && dependency.Reason.Kind == BusinessBlockRequestKind.Dependency
            && dependency.Reason.ConfigurationOrDependency == "gpu-vram-residual-subscription"
            && dependency.Reason.PlanVersion == harness.Controller.CurrentPlanVersion
            && dependency.Interval == TimeSpan.FromSeconds(30)
            && dependency.Outputs.Contains("gpu-vram-residual"));
        Assert.Contains(block.ActiveDependencies, dependency =>
            dependency.Owner == "residual-fast"
            && dependency.Interval == TimeSpan.FromSeconds(1)
            && dependency.Outputs.Contains("gpu-vram-residual"));

        await slow.DisposeAsync();
        Assert.Equal(BusinessBlockState.Running, Block(harness).State);
        Assert.Equal(0, session.DisposeCount);

        await fast.DisposeAsync();
        var result = await harness.Controller.GetCompletion(BlockId).WaitAsync(TestTimeout);
        Assert.Equal(BusinessBlockOutcome.Stopped, result.Outcome);
        Assert.Equal("Idle", harness.Zone.State);
        Assert.Equal(string.Empty, harness.Zone.Message);
        AssertLifecycleRecorded(harness, BusinessBlockTransitionOutcome.Stopped);
        Assert.Equal(1, session.DisposeCount);
        Assert.NotSame(session.ConsumerThread, session.DisposeThread);
        Assert.True(session.ConsumerWasAliveAtDispose);
        await session.ProcessExited.WaitAsync(TestTimeout);
        Assert.False(session.ConsumerThread!.IsAlive);
        Assert.True(Assert.Single(harness.Observer.Registrations).IsDisposed);
        Assert.Equal(current, Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45))));
    }

    [Fact]
    public async Task NormalSessionCompletionIsDoneAndReadsDoNotRestartTheCompletedRun()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var first = harness.Zone.AcquireSubscription("residual-consumer", TimeSpan.FromSeconds(5));
        await first.Ready.WaitAsync(TestTimeout);
        var session = harness.Sessions.Current;
        await session.ProcessEntered.WaitAsync(TestTimeout);

        session.Complete();
        var result = await harness.Controller.GetCompletion(BlockId).WaitAsync(TestTimeout);
        Assert.Equal(BusinessBlockOutcome.Done, result.Outcome);
        AssertLifecycleRecorded(harness, BusinessBlockTransitionOutcome.Done);
        await session.ProcessExited.WaitAsync(TestTimeout);
        Assert.False(session.ConsumerThread!.IsAlive);

        for (var index = 0; index < 3; index++) _ = harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45));
        var second = harness.Zone.AcquireSubscription("residual-secondary", TimeSpan.FromSeconds(1));
        await second.Ready.WaitAsync(TestTimeout);

        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.Equal(1, session.DisposeCount);
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(BusinessBlockOutcome.Done,
            (await harness.Controller.GetCompletion(BlockId).WaitAsync(TestTimeout)).Outcome);
    }

    [Fact]
    public async Task ActiveDemandDoesNotExpireWhenThereAreNoEvidenceReads()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var dependency = harness.Zone.AcquireSubscription("idle-consumer", TimeSpan.FromSeconds(30));
        await dependency.Ready.WaitAsync(TestTimeout);
        var session = harness.Sessions.Current;
        await session.ProcessEntered.WaitAsync(TestTimeout);

        // The previous five-second self-stop policy must not outlive controller demand.
        await Task.Delay(TimeSpan.FromSeconds(6.25));

        Assert.Equal(BusinessBlockState.Running, Block(harness).State);
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.Equal(0, session.DisposeCount);
        await dependency.DisposeAsync();
        Assert.Equal(BusinessBlockOutcome.Stopped,
            (await harness.Controller.GetCompletion(BlockId).WaitAsync(TestTimeout)).Outcome);
    }

    [Fact]
    public async Task RecordOnlyFailureIsVisibleAndDoesNotRetryUntilDemandIsFullyWithdrawn()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var dependency = harness.Zone.AcquireSubscription("residual-consumer", TimeSpan.FromSeconds(30));
        await dependency.Ready.WaitAsync(TestTimeout);
        var session = harness.Sessions.Current;
        await session.ProcessEntered.WaitAsync(TestTimeout);
        var observedAt = DateTimeOffset.UtcNow;
        harness.Zone.ObserveProcessMemory(41, "game.exe", 32 * 1024 * 1024,
            "VidMmProcessUsageChange", checked((ulong)observedAt.UtcDateTime.ToFileTimeUtc()), observedAt);
        Assert.NotEmpty(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));
        var error = new IOException("injected VidMm consumer failure");

        session.Complete(error);
        var result = await harness.Controller.GetCompletion(BlockId).WaitAsync(TestTimeout);
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(typeof(IOException).FullName, result.FailureType);
        Assert.Equal(BusinessBlockFailureStage.Run, result.FailureStage);
        Assert.Equal("Unavailable", harness.Zone.State);
        Assert.Equal("etw-session-failed", harness.Zone.Message);
        Assert.Empty(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));
        Assert.Equal(BusinessBlockFailurePolicy.RecordOnly, Block(harness).Policy.OnFailure);
        AssertLifecycleRecorded(harness, BusinessBlockTransitionOutcome.Failed);
        Assert.Contains(harness.Observer.Failures, failure => failure.Error == error);
        Assert.Equal(1, Block(harness).ActiveDependencies.Count);

        _ = harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45));
        var additional = harness.Zone.AcquireSubscription("residual-additional", TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => additional.Ready.WaitAsync(TestTimeout));
        Assert.Equal(1, harness.Sessions.Attempts);

        await dependency.DisposeAsync();
        await additional.DisposeAsync();
        var restarted = harness.Zone.AcquireSubscription("residual-after-withdrawal", TimeSpan.FromSeconds(1));
        await restarted.Ready.WaitAsync(TestTimeout);
        Assert.Equal(2, harness.Sessions.Attempts);
        await restarted.DisposeAsync();
    }

    [Fact]
    public async Task ConfiguredStopDependentsFailurePolicyWithdrawsTheControllerDependency()
    {
        var options = new BusinessBlockOptions
        {
            Blocks = new(StringComparer.Ordinal)
            {
                [BlockId] = new() { OnFailure = BusinessBlockFailurePolicy.StopDependents }
            }
        };
        await using var harness = VidMmBusinessBlockTestHarness.Create(options);
        var dependency = harness.Zone.AcquireSubscription("residual-consumer", TimeSpan.FromSeconds(30));
        await dependency.Ready.WaitAsync(TestTimeout);
        var session = harness.Sessions.Current;
        await session.ProcessEntered.WaitAsync(TestTimeout);

        session.Complete(new IOException("configured VidMm failure"));
        Assert.Equal(BusinessBlockOutcome.Failed,
            (await harness.Controller.GetCompletion(BlockId).WaitAsync(TestTimeout)).Outcome);
        await Assert.ThrowsAnyAsync<Exception>(() => dependency.Released.WaitAsync(TestTimeout));

        Assert.Equal(BusinessBlockFailurePolicy.StopDependents, Block(harness).Policy.OnFailure);
        Assert.Empty(Block(harness).ActiveDependencies);
        AssertLifecycleRecorded(harness, BusinessBlockTransitionOutcome.Failed);
        Assert.Equal(1, harness.Sessions.Attempts);
        _ = harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45));
        Assert.Equal(1, harness.Sessions.Attempts);
    }

    [Fact]
    public async Task ANewFirstDependencyExplicitlyRestartsButEvidenceReadsDoNot()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var first = harness.Zone.AcquireSubscription("residual-first", TimeSpan.FromSeconds(10));
        await first.Ready.WaitAsync(TestTimeout);
        var firstSession = harness.Sessions.Current;
        await firstSession.ProcessEntered.WaitAsync(TestTimeout);
        var now = DateTimeOffset.UtcNow;
        harness.Zone.ObserveProcessMemory(41, "game.exe", 16 * 1024 * 1024,
            "VidMmProcessUsageChange", checked((ulong)now.UtcDateTime.ToFileTimeUtc()), now);

        await first.DisposeAsync();
        Assert.Equal(BusinessBlockOutcome.Stopped,
            (await harness.Controller.GetCompletion(BlockId).WaitAsync(TestTimeout)).Outcome);
        for (var index = 0; index < 3; index++) _ = harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45));
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.NotEmpty(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));

        var second = harness.Zone.AcquireSubscription("residual-new-run", TimeSpan.FromSeconds(1));
        await second.Ready.WaitAsync(TestTimeout);
        var secondSession = harness.Sessions.Current;
        await secondSession.ProcessEntered.WaitAsync(TestTimeout);
        Assert.Equal(2, harness.Sessions.Attempts);
        Assert.NotEmpty(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));
        Assert.Equal(2, harness.Sessions.Attempts);

        await second.DisposeAsync();
    }

    [Fact]
    public async Task FreezeAndThawUseControllerConfigurationAndRetainTheCurrentEvidence()
    {
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var dependency = harness.Zone.AcquireSubscription("residual-consumer", TimeSpan.FromSeconds(30));
        await dependency.Ready.WaitAsync(TestTimeout);
        var firstSession = harness.Sessions.Current;
        await firstSession.ProcessEntered.WaitAsync(TestTimeout);
        var now = DateTimeOffset.UtcNow;
        harness.Zone.ObserveProcessMemory(41, "game.exe", 32 * 1024 * 1024,
            "VidMmProcessUsageChange", checked((ulong)now.UtcDateTime.ToFileTimeUtc()), now);
        var beforeFreeze = Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45)));

        await harness.Zone.ApplyModeAsync(ResourceManagerComputeZoneMode.Freeze).WaitAsync(TestTimeout);
        Assert.False(Block(harness).Enabled);
        Assert.Equal(BusinessBlockOutcome.Stopped,
            (await harness.Controller.GetCompletion(BlockId).WaitAsync(TestTimeout)).Outcome);
        Assert.Equal(beforeFreeze, Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45))));
        Assert.Equal(1, firstSession.DisposeCount);

        await harness.Zone.ApplyModeAsync(ResourceManagerComputeZoneMode.Normal).WaitAsync(TestTimeout);
        Assert.True(Block(harness).Enabled);
        Assert.Equal(2, harness.Sessions.Attempts);
        Assert.Equal(beforeFreeze, Assert.Single(harness.Zone.ReadEvidence(TimeSpan.FromSeconds(45))));
        Assert.Equal("Running", harness.Zone.State);

        await dependency.DisposeAsync();
    }

    private static BusinessBlockFacts Block(VidMmBusinessBlockTestHarness harness)
        => Assert.Single(harness.Controller.Read().Blocks, block => block.Id == BlockId);

    private static void AssertLifecycleRecorded(
        VidMmBusinessBlockTestHarness harness,
        BusinessBlockTransitionOutcome expected)
    {
        Assert.Contains(harness.Controller.Read().History, transition =>
            transition.BlockId == BlockId && transition.Outcome == expected);
        Assert.Contains(harness.ControllerLogger.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && entry.Message.Contains($"outcome={expected}", StringComparison.Ordinal));
    }
}
