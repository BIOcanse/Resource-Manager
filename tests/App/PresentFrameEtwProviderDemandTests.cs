using Microsoft.Diagnostics.Tracing;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class PresentFrameEtwProviderDemandTests
{
    private static readonly Guid[] ApiProviders =
    [PresentFrameEtwSource.KernelProcessProvider, PresentFrameEtwSource.DxgiProvider, PresentFrameEtwSource.Direct3D9Provider];
    private static readonly Guid[] DisplayProviders =
    [PresentFrameEtwSource.GraphicsKernelProvider, PresentFrameEtwSource.Win32kProvider];
    private static readonly GpuPlacementProcessInstance Process =
        new(73, (ulong)DateTime.UnixEpoch.ToFileTimeUtc(), "recorded", "C:\\recorded.exe");

    [Fact]
    public async Task ApiOnlyEnablesOnlyItsThreeProvidersAndReadsDoNotReenableThem()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        var session = Assert.Single(fixture.Sessions.Snapshot());
        Assert.Equal(ApiProviders, session.Enabled);
        Assert.Collection(session.Operations,
            item => AssertEnable(item, ApiProviders[0], TraceEventLevel.Informational, 0x10, [1, 2]),
            item => AssertEnable(item, ApiProviders[1], TraceEventLevel.Verbose, 2, [42, 43, 55, 56]),
            item => AssertEnable(item, ApiProviders[2], TraceEventLevel.Verbose, 2, [1, 2]));

        fixture.Source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42,
            Process.ProcessId, DateTime.FromFileTimeUtc((long)Process.ProcessStartKey));
        Assert.Equal(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, api.Read());
        Assert.Equal(3, session.Operations.Length);
        Assert.Equal(0, session.EventsLostReads);
        await PresentFrameEtwTestHarness.ReleaseAsync(api);
        Assert.Equal(1, session.Stops);
    }

    [Fact]
    public async Task FirstFrameAddsDisplayProvidersAndLastFrameRemovesThemOnTheSameSession()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var fixture = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        var session = Assert.Single(fixture.Sessions.Snapshot());
        clock.Advance(TimeSpan.FromSeconds(10));
        var first = fixture.Source.AcquireSubscription();
        var second = fixture.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(first);
        await PresentFrameEtwTestHarness.ReadyAsync(second);
        Assert.Equal(3, fixture.Controller.Read().Demands.Count(demand => demand.BlockId == BackendUids.PresentFrameEtwSource));
        Assert.Equal(ApiProviders.Concat(DisplayProviders), session.Enabled);
        Assert.Collection(session.Operations.Skip(3),
            item => AssertEnable(item, DisplayProviders[0], TraceEventLevel.Verbose,
                0x4000000008000001, [184, 168, 252, 178, 116, 17]),
            item => AssertEnable(item, DisplayProviders[1], TraceEventLevel.Verbose,
                0x8000000400001000, [201, 301]));
        var initial = fixture.Source.Read(TimeSpan.FromSeconds(1))!;
        Assert.False(initial.Complete);
        Assert.Empty(initial.Processes);
        Assert.Empty(initial.DisplayStreams);
        clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1));
        Assert.True(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        await PresentFrameEtwTestHarness.ReleaseAsync(first);
        Assert.Equal(5, session.Operations.Length);
        Assert.Equal(2, fixture.Controller.Read().Demands.Count(demand => demand.BlockId == BackendUids.PresentFrameEtwSource));
        await PresentFrameEtwTestHarness.ReleaseAsync(second);
        Assert.Equal(ApiProviders, session.Enabled);
        Assert.Single(fixture.Controller.Read().Demands, demand => demand.BlockId == BackendUids.PresentFrameEtwSource);
        Assert.Equal(DisplayProviders.Select(provider => new PresentEtwProviderOperation("disable", provider)),
            session.Operations.Skip(5));
        Assert.Equal(0, session.Stops);

        // A new frame observation cannot inherit the completed window from before the gap.
        clock.Advance(TimeSpan.FromSeconds(10));
        var resumed = fixture.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(resumed);
        Assert.False(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        Assert.False(fixture.Source.ReadIntervals(clock.GetUtcNow() - TimeSpan.FromSeconds(1),
            clock.GetUtcNow())!.Complete);
        clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1));
        Assert.True(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        Assert.Single(fixture.Sessions.Snapshot());
        await PresentFrameEtwTestHarness.ReleaseAsync(resumed);
        await PresentFrameEtwTestHarness.ReleaseAsync(api);
        Assert.Empty(fixture.Controller.Read().Demands.Where(demand => demand.BlockId == BackendUids.PresentFrameEtwSource));
    }

    [Fact]
    public async Task FrameFirstThenApiKeepsTheFullProviderSetWithoutReenabling()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var frame = fixture.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        var session = Assert.Single(fixture.Sessions.Snapshot());
        Assert.Equal(ApiProviders.Concat(DisplayProviders), session.Enabled);
        var initialOperations = session.Operations.Length;
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        Assert.True(fixture.Source.Read(TimeSpan.FromSeconds(1)) is not null);
        Assert.Equal(initialOperations, session.Operations.Length);
        await PresentFrameEtwTestHarness.ReleaseAsync(api);
        Assert.Equal(initialOperations, session.Operations.Length);
        Assert.Equal(0, session.Stops);
        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        Assert.Equal(1, session.Stops);
        Assert.Single(fixture.Sessions.Snapshot());
    }

    [Fact]
    public async Task FrameWindowStartsAfterBothDisplayProvidersFinishEnabling()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var fixture = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        var session = Assert.Single(fixture.Sessions.Snapshot());
        session.AfterEnable = _ => clock.Advance(TimeSpan.FromSeconds(2));
        var frame = fixture.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        Assert.False(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1));
        Assert.True(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        Assert.Single(fixture.Sessions.Snapshot());
        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        await PresentFrameEtwTestHarness.ReleaseAsync(api);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothLeaseDisposalOrdersStopOnlyAfterTheLastLease(bool frameLeavesFirst)
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        var frame = fixture.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        var session = Assert.Single(fixture.Sessions.Snapshot());
        if (frameLeavesFirst) await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        else await PresentFrameEtwTestHarness.ReleaseAsync(api);
        Assert.Equal(0, session.Stops);
        Assert.Equal(frameLeavesFirst ? ApiProviders : ApiProviders.Concat(DisplayProviders), session.Enabled);
        if (frameLeavesFirst) await PresentFrameEtwTestHarness.ReleaseAsync(api);
        else await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        Assert.Equal(1, session.Stops);
        Assert.True(session.ProcessExited.IsCompleted);
        var retained = fixture.Source.Read(TimeSpan.FromSeconds(1));
        Assert.NotNull(retained);
        Assert.Equal(retained.ObservedAt, fixture.Source.Read(TimeSpan.FromSeconds(1))!.ObservedAt);
        Assert.Single(fixture.Sessions.Snapshot());
        Assert.Equal(1, session.Stops);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task FreezeStopsAndThawStartsOnlyTheProviderSetNeededBySurvivingLeases(bool includeApi, bool includeFrames)
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        IDisposable? api = includeApi ? fixture.Source.AcquireGraphicsApiSubscription(Process) : null;
        IDisposable? frame = includeFrames ? fixture.Source.AcquireSubscription() : null;
        if (api is not null) await PresentFrameEtwTestHarness.ReadyAsync(api);
        if (frame is not null) await PresentFrameEtwTestHarness.ReadyAsync(frame);
        var session = Assert.Single(fixture.Sessions.Snapshot());
        await fixture.Source.ApplyModeAsync(ResourceManagerComputeZoneMode.Freeze).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, session.Stops);
        Assert.True(session.ProcessExited.IsCompleted);
        Assert.Equal(includeFrames, fixture.Source.Read(TimeSpan.FromSeconds(1)) is not null);
        Assert.Single(fixture.Sessions.Snapshot());
        await fixture.Source.ApplyModeAsync(ResourceManagerComputeZoneMode.Normal).WaitAsync(TimeSpan.FromSeconds(10));
        if (api is not null) await PresentFrameEtwTestHarness.ReadyAsync(api);
        if (frame is not null) await PresentFrameEtwTestHarness.ReadyAsync(frame);
        var sessions = fixture.Sessions.Snapshot();
        Assert.Equal(2, sessions.Length);
        var current = fixture.Sessions.Snapshot()[1];
        Assert.Equal(includeFrames ? ApiProviders.Concat(DisplayProviders) : ApiProviders, current.Enabled);
        if (includeFrames) Assert.False(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        if (frame is not null) await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        if (api is not null) await PresentFrameEtwTestHarness.ReleaseAsync(api);
    }

    [Fact]
    public async Task FrameAndApiLeasesAddedWhileFrozenStartTogetherWhenThawed()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        await fixture.Source.ApplyModeAsync(ResourceManagerComputeZoneMode.Freeze).WaitAsync(TimeSpan.FromSeconds(10));
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        var frame = fixture.Source.AcquireSubscription();
        var apiReady = PresentFrameEtwTestHarness.ReadyAsync(api);
        var frameReady = PresentFrameEtwTestHarness.ReadyAsync(frame);
        Assert.False(apiReady.IsCompleted);
        Assert.False(frameReady.IsCompleted);
        Assert.Empty(fixture.Sessions.Snapshot());
        await fixture.Source.ApplyModeAsync(ResourceManagerComputeZoneMode.Normal).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(apiReady, frameReady).WaitAsync(TimeSpan.FromSeconds(10));
        var session = Assert.Single(fixture.Sessions.Snapshot());
        Assert.Equal(ApiProviders.Concat(DisplayProviders), session.Enabled);
        Assert.False(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        await PresentFrameEtwTestHarness.ReleaseAsync(api);
    }

    [Fact]
    public async Task ProviderStartupFailureReleasesItsSessionAndParentRetriesTheExistingDemand()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        fixture.Sessions.Configure = (session, attempt) =>
        {
            if (attempt == 1) session.FailTransition = ("enable", DisplayProviders[0]);
        };
        var frame = fixture.Source.AcquireSubscription();
        var result = await fixture.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Start, result.FailureStage);
        var old = fixture.Sessions.Snapshot()[0];
        Assert.Equal(1, old.Stops);
        await EtwTestRunRecorder.UntilAsync(() => fixture.Sessions.Attempts == 2);
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        Assert.NotNull(fixture.Source.Read(TimeSpan.FromSeconds(1)));
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        Assert.Equal(2, fixture.Sessions.Attempts);
        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        await PresentFrameEtwTestHarness.ReleaseAsync(api);
    }

    [Fact]
    public async Task ProviderDisableFailureReleasesTheRunBeforeRestartingTheSurvivingApiDemand()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        var frame = fixture.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(frame);
        var session = fixture.Sessions.Current;
        session.FailTransition = ("disable", DisplayProviders[0]);
        await PresentFrameEtwTestHarness.ReleaseAsync(frame);
        Assert.Equal(BusinessBlockOutcome.Failed, (await fixture.NextResultAsync()).Outcome);
        Assert.Equal(1, session.Stops);
        await EtwTestRunRecorder.UntilAsync(() => fixture.Sessions.Attempts == 2);
        await PresentFrameEtwTestHarness.ReadyAsync(api);
        Assert.Equal(ApiProviders, fixture.Sessions.Current.Enabled);
        Assert.Single(fixture.Controller.Read().Demands, demand => demand.BlockId == BackendUids.PresentFrameEtwSource);
        await PresentFrameEtwTestHarness.ReleaseAsync(api);
    }

    private static void AssertEnable(PresentEtwProviderOperation item, Guid provider, TraceEventLevel level,
        ulong keywords, int[] ids)
    {
        Assert.Equal("enable", item.Kind);
        Assert.Equal(provider, item.Provider);
        Assert.Equal(level, item.Level);
        Assert.Equal(keywords, item.Keywords);
        Assert.Equal(ids, item.EventIds);
    }
}
