using System.Diagnostics.Tracing;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class NativeEtwActualUseHoldTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Guid Boundary = EventSource.GetGuid(typeof(GpuAllocationBoundary));
    private static readonly Guid[] ApiProviders =
    [PresentFrameEtwSource.KernelProcessProvider, PresentFrameEtwSource.DxgiProvider, PresentFrameEtwSource.Direct3D9Provider];
    private static readonly Guid[] FrameProviders =
    [.. ApiProviders, PresentFrameEtwSource.GraphicsKernelProvider, PresentFrameEtwSource.Win32kProvider];

    [Fact]
    public async Task PresentHoldsFollowTheCurrentOutputsAcrossLongRunningNormalFreezeAndThaw()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var harness = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        var history = new List<Hold>();
        Hold Snapshot() => new(harness.Sessions.Snapshot().Count(session => session.Stops == 0),
            harness.Sessions.Snapshot().Where(session => session.Stops == 0)
                .SelectMany(session => session.Enabled).ToArray());

        await RecordStableAsync(history, Snapshot, clock, 0, []);
        Assert.Equal(0, harness.Sessions.Attempts);
        using var api = harness.Source.AcquireGraphicsApiSubscription(
            new GpuPlacementProcessInstance(73, (ulong)DateTime.UnixEpoch.ToFileTimeUtc(), "recorded", "C:\\recorded.exe"));
        await harness.WaitRunningAsync();
        await RecordStableAsync(history, Snapshot, clock, 1, ApiProviders);

        var frames = harness.Source.AcquireSubscription();
        await RecordStableAsync(history, Snapshot, clock, 1, FrameProviders);
        await harness.ReleaseAsync(frames);
        await RecordStableAsync(history, Snapshot, clock, 1, ApiProviders);
        Assert.Equal(1, harness.Sessions.Attempts);

        await harness.Source.ApplyModeAsync(ResourceManagerComputeZoneMode.Freeze).WaitAsync(Timeout);
        await RecordStableAsync(history, Snapshot, clock, 0, []);
        await harness.Source.ApplyModeAsync(ResourceManagerComputeZoneMode.Normal).WaitAsync(Timeout);
        await harness.WaitRunningAsync();
        await RecordStableAsync(history, Snapshot, clock, 1, ApiProviders);
        Assert.Equal(2, harness.Sessions.Attempts);

        await harness.ReleaseAsync(api);
        await RecordStableAsync(history, Snapshot, clock, 0, []);
        Assert.Equal(2, harness.Sessions.Attempts);
        Assert.Equal(new[] { 0, 1, 1, 1, 0, 1, 0 }, history.Select(hold => hold.Sessions));
    }

    [Fact]
    public async Task GpuOnlyHoldsBoundaryDuringRundownAndAllocationEventsDuringLiveUse()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness(timeProvider: clock);
        var history = new List<Hold>();
        var scanHolds = new List<Guid[]>();
        Hold Snapshot() => new(factory.Sessions.Count(session => session.Stops == 0),
            factory.Sessions.Where(session => session.Stops == 0).SelectMany(session => session.Enabled).ToArray());
        factory.Configure = session => session.DuringCapture = () => scanHolds.Add(session.Enabled.ToArray());

        await RecordStableAsync(history, Snapshot, clock, 0, []);
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.Equal(0, factory.Attempts);
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await RecordStableAsync(history, Snapshot, clock, 0, []);
        Assert.Equal(0, factory.Attempts);

        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        await RecordStableAsync(history, Snapshot, clock, 0, []);
        var slowValue = harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes);
        Assert.NotNull(slowValue);
        Assert.Equal(1, factory.Current.Captures);
        Assert.True(factory.Current.Joined);

        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        await RecordStableAsync(history, Snapshot, clock, 1, [GpuAllocationCollector.Provider]);
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        Assert.Equal(1, factory.Current.Captures);
        Assert.DoesNotContain(Boundary, factory.Current.Enabled);
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        await RecordStableAsync(history, Snapshot, clock, 0, []);
        Assert.Equal(2, factory.Current.Captures);
        Assert.All(scanHolds, providers => Assert.True(providers.ToHashSet().SetEquals([Boundary, GpuAllocationCollector.Provider])));
        Assert.Equal(3, scanHolds.Count);

        await harness.StopAsync();
        await RecordStableAsync(history, Snapshot, clock, 0, []);
        Assert.True(factory.Current.Joined);
        Assert.Equal(2, factory.Attempts);
        Assert.All(factory.Sessions, session => { Assert.Equal(1, session.Stops); Assert.True(session.Joined); });
        Assert.Equal(new[] { 0, 0, 0, 1, 0, 0 }, history.Select(hold => hold.Sessions));
    }

    [Fact]
    public async Task GpuCadenceWithdrawalReleasesLiveSessionWithoutWaitingForAnotherDue()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        var prior = harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes);
        harness.Collector.SynchronizeCaptureInterval(TimeSpan.FromSeconds(30));
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, factory.Current.Captures);
        Assert.Equal(1, factory.Current.Stops);
        Assert.True(factory.Current.Joined);
        Assert.Same(prior, harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(BusinessBlockState.Running, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        var next = harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes);
        harness.Collector.SynchronizeCaptureInterval(null);
        Assert.Equal(2, factory.Attempts);
        Assert.True(factory.Current.Joined);
        Assert.Same(next, harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.All(factory.Sessions, session => Assert.Equal(1, session.Stops));
    }

    [Fact]
    public async Task CancelledFiniteGpuCaptureJoinsItsSessionBeforeReturningAndKeepsThePriorValue()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        var prior = harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes);
        Assert.NotNull(prior);
        factory.Configure = session => session.HoldEnd = true;
        using var cancellation = new CancellationTokenSource();
        var capturing = harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), cancellation.Token);
        await factory.Current.FirstEndScanQueued.WaitAsync(Timeout);
        var pending = factory.Current;
        using var joinGate = new ManualResetEventSlim();
        var joining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.BeforeJoin = () => { joining.TrySetResult(); Assert.True(joinGate.Wait(Timeout)); };
        var cancelling = cancellation.CancelAsync();
        try
        {
            await joining.Task.WaitAsync(Timeout);
            Assert.False(capturing.IsCompleted);
        }
        finally { joinGate.Set(); }
        await cancelling;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capturing);
        Assert.True(pending.Joined);
        Assert.Equal(1, pending.Stops);
        Assert.Same(prior, harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(BusinessBlockState.Running, harness.Controller.ReadState(BackendUids.GpuAllocationCollector));
        factory.Configure = null;
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.Equal(3, factory.Attempts);
        Assert.All(factory.Sessions, session => Assert.True(session.Joined));
    }

    [Fact]
    public async Task UnconfirmedFiniteGpuReleaseFailsTheRunAndNeverCreatesAReplacementSession()
    {
        var factory = new GpuAllocationTestSessionFactory
        {
            Configure = session => session.StopResult = new(true, false, true, ["finite release unconfirmed"])
        };
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        var result = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockFailureStage.Stop, result.FailureStage);
        await harness.Fixture.WaitForStateAsync(BackendUids.GpuAllocationCollector, BusinessBlockState.Stopped);
        Assert.NotNull(harness.Availability.ReadFailure(BackendUids.GpuAllocationCollector));
        Assert.True(factory.Current.Joined);
        Assert.Equal(1, factory.Current.Stops);
        Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, factory.Current.Stops);
        Assert.Same(factory.Current, typeof(GpuAllocationCollector).GetField("session",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(harness.Collector));
    }

    [Fact]
    public async Task VidMmFreezeAndWithdrawalReleaseNativeHoldsWhileTheBackendRemainsRunning()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        var history = new List<Hold>();
        // A VidMm session enables its one provider in construction and owns it
        // until disposal; the fake factory records that same lifetime boundary.
        Hold Snapshot()
        {
            var active = harness.Sessions.Snapshot().Count(session => session.DisposeCount == 0);
            return new(active, Enumerable.Repeat(PresentFrameEtwSource.GraphicsKernelProvider, active).ToArray());
        }

        await RecordStableAsync(history, Snapshot, clock, 0, []);
        Assert.Equal(0, harness.Sessions.Attempts);
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BackendUids.DxgkrnlVidMmEtwTelemetryZone, true);
        await harness.Fixture.WaitForStateAsync(BackendUids.DxgkrnlVidMmEtwTelemetryZone, BusinessBlockState.Running);
        await RecordStableAsync(history, Snapshot, clock, 1, [PresentFrameEtwSource.GraphicsKernelProvider]);
        await harness.Zone.ApplyModeAsync(ResourceManagerComputeZoneMode.Freeze).WaitAsync(Timeout);
        await RecordStableAsync(history, Snapshot, clock, 0, []);
        Assert.False(harness.Sessions.Current.ConsumerThread!.IsAlive);
        await harness.Zone.ApplyModeAsync(ResourceManagerComputeZoneMode.Normal).WaitAsync(Timeout);
        await harness.Fixture.WaitForStateAsync(BackendUids.DxgkrnlVidMmEtwTelemetryZone, BusinessBlockState.Running);
        await RecordStableAsync(history, Snapshot, clock, 1, [PresentFrameEtwSource.GraphicsKernelProvider]);
        await harness.Fixture.SetNativeSubscriptionInUseAsync(BackendUids.DxgkrnlVidMmEtwTelemetryZone, false);
        await harness.Fixture.WaitForStateAsync(BackendUids.DxgkrnlVidMmEtwTelemetryZone, BusinessBlockState.Stopped);
        await RecordStableAsync(history, Snapshot, clock, 0, []);
        Assert.Equal(2, harness.Sessions.Attempts);
        Assert.All(harness.Sessions.Snapshot(), session => Assert.False(session.ConsumerThread!.IsAlive));
        Assert.Equal(new[] { 0, 1, 0, 1, 0 }, history.Select(hold => hold.Sessions));
    }

    private static async Task RecordStableAsync(List<Hold> history, Func<Hold> snapshot,
        PresentFrameEtwManualTimeProvider clock, int sessions, Guid[] providers)
    {
        for (var sample = 0; sample < 4; sample++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(25);
            var current = snapshot();
            Assert.Equal(sessions, current.Sessions);
            Assert.Equal(providers.Order(), current.Providers.Order());
        }
        history.Add(snapshot());
    }

    private sealed record Hold(int Sessions, Guid[] Providers);
}
