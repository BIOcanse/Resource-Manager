using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Frames;
using System.Reflection;

namespace Resource_Manager_APP.Tests;

public sealed class PresentFrameEtwLifecycleTests
{
    [Fact]
    public async Task AddingFrameDemandToRunningApiSourceRetainsProviderPermissionFailureAfterCleanup()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var api = harness.Source.AcquireGraphicsApiSubscription(Instance());
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        var session = harness.Sessions.Current;
        await session.ProcessStarted.WaitAsync(TimeSpan.FromSeconds(5));
        session.AfterEnable = _ => throw new UnauthorizedAccessException("injected running frame provider permission failure");
        var frame = harness.Source.AcquireSubscription();
        var result = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Run, result.FailureStage);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, result.FailureType);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(PresentFrameEtwSource.BusinessBlockId) == BusinessBlockState.Stopped);
        await harness.Source.PublishAvailabilityAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.Equal(1, session.Stops);
        Assert.False(session.HasBoundHandler);
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.False(session.ConsumerThread!.IsAlive);
        Assert.False(Assert.Single(harness.Controller.Read().Facts, fact => fact.Key == PresentFrameEtwSource.AvailabilityFact).Value);
        Assert.Equal(2, harness.Controller.Read().Demands.Count);
        Assert.Empty(harness.Diagnostics.Read(PresentFrameEtwSource.BusinessBlockId).Metrics.ActiveThreads);
        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        await PresentFrameEtwTestHarness.ReleaseAsync(api);
    }

    [Fact]
    public async Task ProviderPermissionFailureRemainsUnavailableAfterConfirmedSessionCleanup()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        harness.Sessions.Configure = (session, _) => session.AfterEnable = provider =>
            throw new UnauthorizedAccessException("injected post-creation provider permission failure");
        var subscription = harness.Source.AcquireSubscription();
        var result = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Start, result.FailureStage);
        Assert.Equal(typeof(UnauthorizedAccessException).FullName, result.FailureType);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(PresentFrameEtwSource.BusinessBlockId) == BusinessBlockState.Stopped);
        await harness.Source.PublishAvailabilityAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.Equal(1, harness.Sessions.Current.Stops);
        Assert.False(harness.Sessions.Current.HasBoundHandler);
        Assert.False(Assert.Single(harness.Controller.Read().Facts, fact => fact.Key == PresentFrameEtwSource.AvailabilityFact).Value);
        Assert.Single(harness.Controller.Read().Demands);
        Assert.Empty(harness.Diagnostics.Read(PresentFrameEtwSource.BusinessBlockId).Metrics.ActiveThreads);
        await PresentFrameEtwTestHarness.ReleaseAsync(subscription);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeUnavailabilityReturnsAndPreventsRepeatedStartsWithHeldDemand(bool releaseFailure)
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        if (!releaseFailure)
            harness.Sessions.Configure = (_, _) => throw new UnauthorizedAccessException("injected native permission failure");
        var subscription = harness.Source.AcquireSubscription();
        if (releaseFailure)
        {
            await PresentFrameEtwTestHarness.ReadyAsync(subscription);
            harness.Sessions.Current.DisposeFailure = new IOException("injected native release failure");
            harness.Sessions.Current.Complete();
        }
        Assert.Equal(BusinessBlockOutcome.Failed, (await harness.NextResultAsync()).Outcome);
        await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(PresentFrameEtwSource.BusinessBlockId) == BusinessBlockState.Stopped);
        await harness.Source.PublishAvailabilityAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, harness.Sessions.Attempts);
        Assert.False(Assert.Single(harness.Controller.Read().Facts, fact => fact.Key == PresentFrameEtwSource.AvailabilityFact).Value);
        Assert.Empty(harness.Diagnostics.Read(PresentFrameEtwSource.BusinessBlockId).Metrics.ActiveThreads);
        Assert.Single(harness.Controller.Read().Demands);
        await PresentFrameEtwTestHarness.ReleaseAsync(subscription);
    }

    [Fact]
    public async Task StopRetainsObservedFrameValuesEvenAfterAFutureCursorRead()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var harness = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        var frame = harness.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        var ledger = Assert.IsType<PresentFrameLedger>(typeof(PresentFrameEtwSource)
            .GetField("ledger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Source));
        var at = clock.GetUtcNow().UtcTicks;
        ledger.ProcessStarted(41, 1);
        ledger.Present(41, FramePresentSource.Dxgi, 3, at + TimeSpan.FromMilliseconds(10).Ticks);
        ledger.Present(41, FramePresentSource.Dxgi, 3, at + TimeSpan.FromMilliseconds(26).Ticks);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        var before = Assert.Single(Assert.Single(harness.Source.Read(TimeSpan.FromSeconds(1))!.Processes).Streams);
        Assert.Equal(2, before.PresentCount);

        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        clock.Advance(TimeSpan.FromMinutes(1));
        var future = harness.Source.ReadIntervals(clock.GetUtcNow().AddSeconds(-1), clock.GetUtcNow());
        Assert.False(future!.Complete);
        var retained = Assert.Single(Assert.Single(harness.Source.Read(TimeSpan.FromSeconds(1))!.Processes).Streams);
        Assert.Equal(before.LastPresentAt, retained.LastPresentAt);
        Assert.Equal(before.PresentCount, retained.PresentCount);
    }

    [Fact]
    public async Task LastDependencyWithdrawalReturnsStoppedAfterReleasingTheConsumerThread()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var frame = harness.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        var session = Assert.Single(harness.Sessions.Snapshot());
        await session.ProcessStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(harness.Diagnostics.Read("present-etw").Metrics.ActiveThreads);

        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        var completion = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Stopped, completion.Outcome);
        Assert.Equal(1, session.Stops);
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.False(session.ConsumerThread!.IsAlive);
        Assert.Empty(harness.Diagnostics.Read("present-etw").Metrics.ActiveThreads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessingReturnReleasesTheThreadAndParentRestartsHeldDemand(bool fail)
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var api = harness.Source.AcquireGraphicsApiSubscription(Instance());
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        var session = harness.Sessions.Current;
        await session.ProcessStarted.WaitAsync(TimeSpan.FromSeconds(5));
        session.Complete(fail ? new IOException("injected consumer failure") : null);
        var completion = await harness.NextResultAsync();
        Assert.Equal(fail ? BusinessBlockOutcome.Failed : BusinessBlockOutcome.Done, completion.Outcome);
        if (fail) Assert.Equal(BusinessBlockFailureStage.Run, completion.FailureStage);
        Assert.Equal(1, session.Stops);
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.False(session.ConsumerThread!.IsAlive);
        await EtwTestRunRecorder.UntilAsync(() => harness.Sessions.Attempts == 2);
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        Assert.False(PresentFrameEtwTestHarness.Dependency(api).Released.IsCompleted);
        var additional = harness.Source.AcquireGraphicsApiSubscription(Instance(74));
        await PresentFrameEtwTestHarness.ReadyAsync(additional);
        Assert.Equal(2, harness.Sessions.Attempts);
        await PresentFrameEtwTestHarness.ReleaseAsync(api);
        await PresentFrameEtwTestHarness.ReleaseAsync(additional);
    }

    [Fact]
    public async Task ControllerQueryAndReadsDoNotCreateASessionOrReadNativeLossState()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var harness = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        Assert.Null(harness.Source.Read(TimeSpan.FromSeconds(1)));
        Assert.Null(harness.Source.ReadIntervals(clock.GetUtcNow() - TimeSpan.FromSeconds(1), clock.GetUtcNow()));
        for (var index = 0; index < 3; index++) _ = harness.Controller.Read();
        Assert.Empty(harness.Sessions.Snapshot());

        var frame = harness.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        var session = Assert.Single(harness.Sessions.Snapshot());
        session.ReportedEventsLost = 1;
        _ = harness.Source.Read(TimeSpan.FromSeconds(1));
        _ = harness.Source.ReadIntervals(clock.GetUtcNow() - TimeSpan.FromSeconds(1), clock.GetUtcNow());
        Assert.Equal(0, session.EventsLostReads);
        Assert.Equal(0, session.Flushes);

        clock.Advance(PresentFrameEtwSource.FlushInterval);
        Assert.Equal(1, session.Flushes);
        Assert.Equal(1, session.EventsLostReads);
        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        clock.Advance(PresentFrameEtwSource.FlushInterval);
        Assert.Equal(1, session.Flushes);
    }

    [Fact]
    public async Task ProducerFlushFailureReleasesItsThreadAndParentRestartsCurrentDemand()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var harness = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        var frame = harness.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        var session = Assert.Single(harness.Sessions.Snapshot());
        session.FlushFailure = new IOException("injected flush failure");
        var ledger = Assert.IsType<PresentFrameLedger>(typeof(PresentFrameEtwSource)
            .GetField("ledger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Source));
        ledger.ProcessStarted(41, 1);
        var now = clock.GetUtcNow().UtcTicks;
        ledger.Present(41, FramePresentSource.Dxgi, 3, now - TimeSpan.FromMilliseconds(16).Ticks);
        ledger.Present(41, FramePresentSource.Dxgi, 3, now);
        var before = harness.Source.Read(TimeSpan.FromSeconds(1))!;
        Assert.NotEmpty(before.Processes);
        var stream = Assert.Single(Assert.Single(before.Processes).Streams);
        Assert.Equal(2, stream.PresentCount);
        Assert.Equal(1, stream.Statistics.IntervalCount);
        Assert.Equal(16d, stream.Statistics.DurationMs);

        clock.Advance(PresentFrameEtwSource.FlushInterval);
        var completion = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, completion.Outcome);
        Assert.Equal(1, session.Flushes);
        Assert.Equal(0, session.EventsLostReads);
        Assert.Equal(1, session.Stops);
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.False(session.ConsumerThread!.IsAlive);
        await EtwTestRunRecorder.UntilAsync(() => harness.Sessions.Attempts == 2);
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        Assert.Equal(2, harness.Sessions.Attempts);
        Assert.Empty(harness.Source.Read(TimeSpan.FromSeconds(1))!.Processes);
        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
    }

    [Fact]
    public async Task FreezeUsesTheSourceModePathAndRetainsItsTypedFrameTimestamp()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var harness = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        var frame = harness.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        var session = Assert.Single(harness.Sessions.Snapshot());
        var before = harness.Source.Read(TimeSpan.FromSeconds(1));
        Assert.NotNull(before);

        await harness.Source.ApplyModeAsync(ResourceManagerComputeZoneMode.Freeze);
        Assert.Equal(1, session.Stops);
        var stopped = harness.Source.Read(TimeSpan.FromSeconds(1));
        Assert.NotNull(stopped);
        Assert.Equal(before.ObservedAt, stopped.ObservedAt);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(stopped.ObservedAt, harness.Source.Read(TimeSpan.FromSeconds(1))!.ObservedAt);

        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
    }

    private static GpuPlacementProcessInstance Instance(int pid = 73)
        => new(pid, (ulong)DateTime.UnixEpoch.ToFileTimeUtc(), "recorded", "C:\\recorded.exe");
}
