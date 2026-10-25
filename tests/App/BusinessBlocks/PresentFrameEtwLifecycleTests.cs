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
        Assert.Single(Assert.Single(harness.Controller.Read().Blocks).Metrics.ActiveThreads);

        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        var completion = await harness.Controller.GetCompletion("present-etw");
        Assert.Equal(BusinessBlockOutcome.Stopped, completion.Outcome);
        Assert.Equal(1, session.Stops);
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.False(session.ConsumerThread!.IsAlive);
        Assert.Empty(Assert.Single(harness.Controller.Read().Blocks).Metrics.ActiveThreads);
    }

    [Fact]
    public async Task ReadsAndAdditionalDependenciesDoNotRestartANormallyCompletedRun()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var harness = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        var frame = harness.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        var session = Assert.Single(harness.Sessions.Snapshot());
        var last = harness.Source.Read(TimeSpan.FromSeconds(1));
        Assert.NotNull(last);

        session.Complete();
        var completion = await harness.Controller.GetCompletion("present-etw");
        Assert.Equal(BusinessBlockOutcome.Done, completion.Outcome);
        Assert.Equal(1, session.Stops);

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(last.ObservedAt, harness.Source.Read(TimeSpan.FromSeconds(1))!.ObservedAt);
        Assert.NotNull(harness.Source.ReadIntervals(clock.GetUtcNow() - TimeSpan.FromSeconds(1), clock.GetUtcNow()));

        var second = harness.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(second);
        _ = harness.Source.Read(TimeSpan.FromSeconds(1));
        _ = harness.Source.ReadIntervals(clock.GetUtcNow() - TimeSpan.FromSeconds(1), clock.GetUtcNow());
        Assert.Equal(1, harness.Sessions.Attempts);

        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        await PresentFrameEtwTestHarness.ReleaseAsync(second);
        var restarted = harness.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(restarted);
        Assert.Equal(2, harness.Sessions.Attempts);
        await PresentFrameEtwTestHarness.ReleaseAsync(restarted);
    }

    [Fact]
    public async Task SessionFailureIsReportedAndAnExistingDependencyDoesNotRetry()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var api = harness.Source.AcquireGraphicsApiSubscription(Instance());
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        var session = Assert.Single(harness.Sessions.Snapshot());
        Assert.Equal(BusinessBlockFailurePolicy.RecordOnly,
            Assert.Single(harness.Controller.Read().Blocks).Policy.OnFailure);

        session.Complete(new IOException("injected consumer failure"));
        var completion = await harness.Controller.GetCompletion("present-etw");
        Assert.Equal(BusinessBlockOutcome.Failed, completion.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Run, completion.FailureStage);
        Assert.Equal(1, session.Stops);
        Assert.True(session.ProcessExited.IsCompleted);

        _ = api.Read();
        _ = harness.Source.Read(TimeSpan.FromSeconds(1));
        _ = harness.Source.ReadIntervals(DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow);
        var additional = harness.Source.AcquireGraphicsApiSubscription(Instance(74));
        await Assert.ThrowsAnyAsync<Exception>(() => PresentFrameEtwTestHarness.ReadyAsync(additional));
        Assert.Equal(1, harness.Sessions.Attempts);

        await PresentFrameEtwTestHarness.ReleaseAsync(api);
        await PresentFrameEtwTestHarness.ReleaseAsync(additional);
        var next = harness.Source.AcquireGraphicsApiSubscription(Instance(75));
        await PresentFrameEtwTestHarness.ReadyAsync(next);
        Assert.Equal(2, harness.Sessions.Attempts);
        await PresentFrameEtwTestHarness.ReleaseAsync(next);
    }

    [Fact]
    public async Task ConfiguredStopDependentsPolicyWithdrawsTheFailedPresentDependency()
    {
        var options = new BusinessBlockOptions
        {
            Blocks = new(StringComparer.Ordinal)
            {
                ["present-etw"] = new() { OnFailure = BusinessBlockFailurePolicy.StopDependents }
            }
        };
        await using var harness = PresentFrameEtwTestHarness.Create(blockOptions: options);
        var api = harness.Source.AcquireGraphicsApiSubscription(Instance());
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        var dependency = PresentFrameEtwTestHarness.Dependency(api);

        harness.Sessions.Current.Complete(new IOException("configured failure"));
        var completion = await harness.Controller.GetCompletion("present-etw");
        Assert.Equal(BusinessBlockOutcome.Failed, completion.Outcome);
        await Assert.ThrowsAnyAsync<Exception>(() => dependency.Released);
        Assert.Empty(Assert.Single(harness.Controller.Read().Blocks).ActiveDependencies);
        Assert.Equal(BusinessBlockFailurePolicy.StopDependents,
            Assert.Single(harness.Controller.Read().Blocks).Policy.OnFailure);
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
    public async Task ProducerFlushFailureFailsTheRunAndDoesNotRetryWhileItsDependencyRemains()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var harness = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        var frame = harness.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        var session = Assert.Single(harness.Sessions.Snapshot());
        session.FlushFailure = new IOException("injected flush failure");

        clock.Advance(PresentFrameEtwSource.FlushInterval);
        var completion = await harness.Controller.GetCompletion("present-etw");
        Assert.Equal(BusinessBlockOutcome.Failed, completion.Outcome);
        Assert.Equal(1, session.Flushes);
        Assert.Equal(0, session.EventsLostReads);
        Assert.Equal(1, session.Stops);
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.Null(harness.Source.Read(TimeSpan.FromSeconds(1)));

        _ = harness.Source.ReadIntervals(clock.GetUtcNow() - TimeSpan.FromSeconds(1), clock.GetUtcNow());
        Assert.Equal(1, harness.Sessions.Attempts);
        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        var next = harness.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(next);
        Assert.Equal(2, harness.Sessions.Attempts);
        await PresentFrameEtwTestHarness.ReleaseAsync(next);
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
