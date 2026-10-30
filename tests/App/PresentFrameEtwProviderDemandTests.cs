using Microsoft.Diagnostics.Tracing;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class PresentFrameEtwProviderDemandTests
{
    [Fact]
    public async Task StoppedInstanceDoesNotTurnAnObservedPresentIntoANegativeImageResult()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var lease = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        fixture.Source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42, Process.ProcessId,
            DateTime.FromFileTimeUtc((long)Process.ProcessStartKey));
        fixture.Source.ObserveGraphicsApiProcessLifetime(Process.ProcessId, null);
        Assert.Null(lease.Read());
        Assert.Equal(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, lease.Complete());
        await EtwTestRunRecorder.UntilAsync(() => fixture.Sessions.Current.Stops == 1);
    }

    [Fact]
    public async Task CompletingALeaseReturnsTheFinalPresentBeforeRemovingIt()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var lease = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        fixture.Source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42, Process.ProcessId,
            DateTime.FromFileTimeUtc((long)Process.ProcessStartKey));
        Assert.Equal(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, lease.Complete());
        Assert.Null(lease.Read());
        Assert.Null(lease.Complete());
        Assert.Empty(fixture.Source.ReadHolders());
        await EtwTestRunRecorder.UntilAsync(() => fixture.Sessions.Current.Stops == 1);
    }

    [Fact]
    public async Task PidFiltersUpdateInPlaceAndAreRemovedForFramesAndAboveEightPids()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var leases = new List<IDisposable> { fixture.Source.AcquireGraphicsApiSubscription(Process) };
        await fixture.WaitRunningAsync();
        var session = Assert.Single(fixture.Sessions.Snapshot());
        void AssertFilter(int[]? pids)
        {
            foreach (var provider in new[] { PresentFrameEtwSource.DxgiProvider, PresentFrameEtwSource.Direct3D9Provider })
                Assert.Equal(pids, session.Operations.Last(item => item.Provider == provider && item.Kind == "enable").ProcessIds);
        }
        AssertFilter([73]);
        leases.Add(fixture.Source.AcquireGraphicsApiSubscription(Process with { ProcessId = 74 }));
        AssertFilter([73, 74]);
        for (var pid = 75; pid <= 80; pid++)
            leases.Add(fixture.Source.AcquireGraphicsApiSubscription(Process with { ProcessId = pid }));
        AssertFilter([73, 74, 75, 76, 77, 78, 79, 80]);
        var ninth = fixture.Source.AcquireGraphicsApiSubscription(Process with { ProcessId = 81 });
        AssertFilter(null);
        await fixture.ReleaseAsync(ninth);
        AssertFilter([73, 74, 75, 76, 77, 78, 79, 80]);
        var frame = fixture.Source.AcquireSubscription();
        AssertFilter(null);
        await fixture.ReleaseAsync(frame);
        AssertFilter([73, 74, 75, 76, 77, 78, 79, 80]);
        await fixture.ReleaseAsync(leases[0]);
        leases.RemoveAt(0);
        AssertFilter([74, 75, 76, 77, 78, 79, 80]);
        Assert.Single(fixture.Sessions.Snapshot());
        Assert.Equal(0, session.Stops);
        foreach (var lease in leases) await fixture.ReleaseAsync(lease);
        Assert.Empty(session.Enabled);
        Assert.Equal(1, session.Stops);
    }

    private static readonly Guid[] ApiProviders =
    [PresentFrameEtwSource.DxgiProvider, PresentFrameEtwSource.Direct3D9Provider];
    private static readonly Guid[] DisplayProviders =
    [PresentFrameEtwSource.KernelProcessProvider, PresentFrameEtwSource.GraphicsKernelProvider, PresentFrameEtwSource.Win32kProvider];
    private static readonly GpuPlacementProcessInstance Process =
        new(73, (ulong)DateTime.UnixEpoch.ToFileTimeUtc(), "recorded", "C:\\recorded.exe");

    [Fact]
    public async Task ApiOnlyEnablesOnlyPendingKindsAndReleasesBeforeTheCachedAnswerIsRead()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        var session = Assert.Single(fixture.Sessions.Snapshot());
        Assert.Equal(ApiProviders, session.Enabled);
        Assert.Collection(session.Operations,
            item => AssertEnable(item, ApiProviders[0], TraceEventLevel.Verbose, 2, [42, 55]),
            item => AssertEnable(item, ApiProviders[1], TraceEventLevel.Verbose, 2, [1]));

        fixture.Source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42,
            Process.ProcessId, DateTime.FromFileTimeUtc((long)Process.ProcessStartKey));
        await EtwTestRunRecorder.UntilAsync(() => session.Stops == 1);
        Assert.False(fixture.Source.HasActiveSubscriptions);
        Assert.Empty(session.Enabled);
        var retained = Assert.Single(fixture.Source.ReadHolders());
        Assert.False(retained.NeedsEvents);
        Assert.True(retained.HasCurrent);
        Assert.Equal(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, api.Read());
        Assert.Equal(4, session.Operations.Length);
        Assert.Equal(0, session.EventsLostReads);
        await fixture.ReleaseAsync(api);
        Assert.Equal(1, session.Stops);
    }

    [Fact]
    public async Task PendingApiUseShrinksAndCompletedLeasesNeverRestartTheSession()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var transitions = new List<PresentFrameEtwSource.PresentHolder[]>();
        fixture.Source.HoldersChanged += () => transitions.Add(fixture.Source.ReadHolders());
        var first = fixture.Source.AcquireGraphicsApiSubscription(Process);
        var secondProcess = Process with { ProcessId = 74 };
        var second = fixture.Source.AcquireGraphicsApiSubscription(secondProcess);
        await fixture.WaitRunningAsync();
        var session = Assert.Single(fixture.Sessions.Snapshot());
        Assert.Equal(new[] { 73, 74 }, fixture.Source.ReadHolders().Where(holder => holder.NeedsEvents)
            .Select(holder => holder.ProcessId!.Value).Order());
        var initialOperations = session.Operations.Length;

        Assert.True(fixture.Source.ObserveGraphicsApiPresent(ApiProviders[0], 42, 73,
            DateTime.FromFileTimeUtc((long)Process.ProcessStartKey)));
        Assert.True(fixture.Source.HasActiveSubscriptions);
        Assert.Equal(74, Assert.Single(fixture.Source.ReadHolders().Where(holder => holder.NeedsEvents)).ProcessId);
        Assert.Equal(initialOperations + 2, session.Operations.Length);
        Assert.False(fixture.Source.ObserveGraphicsApiPresent(ApiProviders[0], 43, 74,
            DateTime.FromFileTimeUtc((long)Process.ProcessStartKey)));
        Assert.False(fixture.Source.ObserveGraphicsApiPresent(ApiProviders[0], 42, 73,
            DateTime.FromFileTimeUtc((long)Process.ProcessStartKey)));
        Assert.True(fixture.Source.ObserveGraphicsApiPresent(ApiProviders[1], 1, 74,
            DateTime.FromFileTimeUtc((long)Process.ProcessStartKey)));
        await EtwTestRunRecorder.UntilAsync(() => session.Stops == 1);
        Assert.False(fixture.Source.HasActiveSubscriptions);
        Assert.All(fixture.Source.ReadHolders(), holder => { Assert.False(holder.NeedsEvents); Assert.True(holder.HasCurrent); });
        Assert.Equal(GpuGraphicsApi.D3D9, second.Read());
        Assert.Equal(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, first.Read());

        var third = fixture.Source.AcquireGraphicsApiSubscription(Process with { ProcessId = 75 });
        await fixture.WaitRunningAsync();
        Assert.Equal(2, fixture.Sessions.Attempts);
        Assert.Equal(GpuGraphicsApi.D3D9, second.Read());
        await fixture.ReleaseAsync(third);
        Assert.Equal(2, fixture.Sessions.Attempts);
        await fixture.ReleaseAsync(first);
        await fixture.ReleaseAsync(second);
        Assert.Empty(fixture.Source.ReadHolders());
        Assert.Contains(transitions, holders => holders.Count(holder => holder.NeedsEvents) == 2);
        Assert.Contains(transitions, holders => holders.Length == 2 && holders.All(holder => !holder.NeedsEvents));
    }

    [Fact]
    public async Task InvalidatedProcessReleasesItsHoldBeforeTheCallerDisposesTheLease()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        var session = Assert.Single(fixture.Sessions.Snapshot());
        Assert.True(fixture.Source.ObserveGraphicsApiProcessLifetime(Process.ProcessId, null));
        await EtwTestRunRecorder.UntilAsync(() => session.Stops == 1);
        Assert.False(fixture.Source.HasActiveSubscriptions);
        Assert.False(Assert.Single(fixture.Source.ReadHolders()).NeedsEvents);
        Assert.Null(api.Read());
        Assert.False(fixture.Source.ObserveGraphicsApiPresent(ApiProviders[0], 42, Process.ProcessId,
            DateTime.FromFileTimeUtc((long)Process.ProcessStartKey)));
        Assert.Single(fixture.Sessions.Snapshot());
        await fixture.ReleaseAsync(api);
    }

    [Fact]
    public async Task FirstFrameAddsDisplayProvidersAndLastFrameRemovesThemOnTheSameSession()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var fixture = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        var session = Assert.Single(fixture.Sessions.Snapshot());
        clock.Advance(TimeSpan.FromSeconds(10));
        var first = fixture.Source.AcquireSubscription();
        var second = fixture.Source.AcquireSubscription();
        await fixture.WaitRunningAsync();
        await fixture.WaitRunningAsync();
        Assert.True(fixture.Source.HasActiveSubscriptions);
        Assert.Equal(ApiProviders.Concat(DisplayProviders).Order(), session.Enabled.Order());
        Assert.Collection(session.Operations.Where(item => item.Kind == "enable" && DisplayProviders.Contains(item.Provider)),
            item => AssertEnable(item, DisplayProviders[0], TraceEventLevel.Informational, 0x10, [1, 2]),
            item => AssertEnable(item, DisplayProviders[1], TraceEventLevel.Verbose,
                0x4000000008000001, [184, 168, 252, 178, 116, 17]),
            item => AssertEnable(item, DisplayProviders[2], TraceEventLevel.Verbose,
                0x8000000400001000, [201, 301]));
        var initial = fixture.Source.Read(TimeSpan.FromSeconds(1))!;
        Assert.False(initial.Complete);
        Assert.Empty(initial.Processes);
        Assert.Empty(initial.DisplayStreams);
        clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1));
        Assert.True(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        await fixture.ReleaseAsync(first);
        Assert.Equal(7, session.Operations.Length);
        Assert.True(fixture.Source.HasActiveSubscriptions);
        await fixture.ReleaseAsync(second);
        Assert.Equal(ApiProviders, session.Enabled);
        Assert.True(fixture.Source.HasActiveSubscriptions);
        Assert.Equal(DisplayProviders.Select(provider => new PresentEtwProviderOperation("disable", provider)),
            session.Operations.Skip(9));
        Assert.Equal(0, session.Stops);

        // A new frame observation cannot inherit the completed window from before the gap.
        clock.Advance(TimeSpan.FromSeconds(10));
        var resumed = fixture.Source.AcquireSubscription();
        await fixture.WaitRunningAsync();
        Assert.False(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        Assert.False(fixture.Source.ReadIntervals(clock.GetUtcNow() - TimeSpan.FromSeconds(1),
            clock.GetUtcNow())!.Complete);
        clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1));
        Assert.True(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        Assert.Single(fixture.Sessions.Snapshot());
        await fixture.ReleaseAsync(resumed);
        await fixture.ReleaseAsync(api);
        Assert.False(fixture.Source.HasActiveSubscriptions);
    }

    [Fact]
    public async Task FrameFirstThenApiKeepsTheFullProviderSetWithoutReenabling()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var frame = fixture.Source.AcquireSubscription();
        await fixture.WaitRunningAsync();
        var session = Assert.Single(fixture.Sessions.Snapshot());
        Assert.Equal(ApiProviders.Concat(DisplayProviders).Order(), session.Enabled.Order());
        var initialOperations = session.Operations.Length;
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        Assert.True(fixture.Source.Read(TimeSpan.FromSeconds(1)) is not null);
        Assert.Equal(initialOperations, session.Operations.Length);
        await fixture.ReleaseAsync(api);
        Assert.Equal(initialOperations, session.Operations.Length);
        Assert.Equal(0, session.Stops);
        await fixture.ReleaseAsync(frame);
        Assert.Equal(1, session.Stops);
        Assert.Single(fixture.Sessions.Snapshot());
    }

    [Fact]
    public async Task FrameWindowStartsAfterBothDisplayProvidersFinishEnabling()
    {
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var fixture = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        var session = Assert.Single(fixture.Sessions.Snapshot());
        session.AfterEnable = _ => clock.Advance(TimeSpan.FromSeconds(2));
        var frame = fixture.Source.AcquireSubscription();
        await fixture.WaitRunningAsync();
        Assert.False(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1));
        Assert.True(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        Assert.Single(fixture.Sessions.Snapshot());
        await fixture.ReleaseAsync(frame);
        await fixture.ReleaseAsync(api);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothLeaseDisposalOrdersStopOnlyAfterTheLastLease(bool frameLeavesFirst)
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        var frame = fixture.Source.AcquireSubscription();
        await fixture.WaitRunningAsync();
        var session = Assert.Single(fixture.Sessions.Snapshot());
        if (frameLeavesFirst) await fixture.ReleaseAsync(frame);
        else await fixture.ReleaseAsync(api);
        Assert.Equal(0, session.Stops);
        Assert.Equal((frameLeavesFirst ? ApiProviders : ApiProviders.Concat(DisplayProviders)).Order(), session.Enabled.Order());
        if (frameLeavesFirst) await fixture.ReleaseAsync(api);
        else await fixture.ReleaseAsync(frame);
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
    public async Task OutputLeasesEnableOnlyTheProviderSetTheyNeed(bool includeApi, bool includeFrames)
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        IDisposable? api = includeApi ? fixture.Source.AcquireGraphicsApiSubscription(Process) : null;
        IDisposable? frame = includeFrames ? fixture.Source.AcquireSubscription() : null;
        if (api is not null) await fixture.WaitRunningAsync();
        if (frame is not null) await fixture.WaitRunningAsync();
        var session = Assert.Single(fixture.Sessions.Snapshot());
        Assert.Equal(0, session.Stops);
        Assert.False(session.ProcessExited.IsCompleted);
        Assert.Equal(includeFrames, fixture.Source.Read(TimeSpan.FromSeconds(1)) is not null);
        var current = Assert.Single(fixture.Sessions.Snapshot());
        Assert.Equal((includeFrames ? ApiProviders.Concat(DisplayProviders) : ApiProviders).Order(), current.Enabled.Order());
        if (includeFrames) Assert.False(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        if (frame is not null) await fixture.ReleaseAsync(frame);
        if (api is not null) await fixture.ReleaseAsync(api);
    }

    [Fact]
    public async Task FrameAndApiLeasesStartTogetherWithTheirRequiredProviders()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        var frame = fixture.Source.AcquireSubscription();
        var apiReady = fixture.WaitRunningAsync();
        var frameReady = fixture.WaitRunningAsync();
        await Task.WhenAll(apiReady, frameReady).WaitAsync(TimeSpan.FromSeconds(10));
        var session = Assert.Single(fixture.Sessions.Snapshot());
        Assert.Equal(ApiProviders.Concat(DisplayProviders).Order(), session.Enabled.Order());
        Assert.False(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        await fixture.ReleaseAsync(frame);
        await fixture.ReleaseAsync(api);
    }

    [Fact]
    public async Task ProviderStartupFailureReleasesItsSessionAndParentRetriesTheExistingDemand()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        fixture.Sessions.Configure = (session, attempt) =>
        {
            if (attempt == 1) session.FailTransition = ("enable", DisplayProviders[1]);
        };
        var frame = fixture.Source.AcquireSubscription();
        var result = await fixture.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Start, result.FailureStage);
        var old = fixture.Sessions.Snapshot()[0];
        Assert.Equal(1, old.Stops);
        await EtwTestRunRecorder.UntilAsync(() => fixture.Sessions.Attempts == 2);
        await fixture.WaitRunningAsync();
        Assert.NotNull(fixture.Source.Read(TimeSpan.FromSeconds(1)));
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        Assert.Equal(2, fixture.Sessions.Attempts);
        await fixture.ReleaseAsync(frame);
        await fixture.ReleaseAsync(api);
    }

    [Fact]
    public async Task ProviderDisableFailureReleasesTheRunBeforeRestartingTheSurvivingApiDemand()
    {
        await using var fixture = PresentFrameEtwTestHarness.Create();
        var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        await fixture.WaitRunningAsync();
        var frame = fixture.Source.AcquireSubscription();
        await fixture.WaitRunningAsync();
        var session = fixture.Sessions.Current;
        session.FailTransition = ("disable", DisplayProviders[0]);
        await fixture.ReleaseAsync(frame);
        Assert.Equal(BusinessBlockOutcome.Failed, (await fixture.NextResultAsync()).Outcome);
        Assert.Equal(1, session.Stops);
        await EtwTestRunRecorder.UntilAsync(() => fixture.Sessions.Attempts == 2);
        await fixture.WaitRunningAsync();
        Assert.Equal(ApiProviders, fixture.Sessions.Current.Enabled);
        Assert.True(fixture.Source.HasActiveSubscriptions);
        await fixture.ReleaseAsync(api);
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
