using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging.Abstractions;
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
    public void ApiOnlyEnablesOnlyItsThreeProvidersAndReadsDoNotReenableThem()
    {
        using var fixture = new Fixture();
        using var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        var session = Assert.Single(fixture.Sessions);
        Assert.Equal(ApiProviders, session.Enabled);
        Assert.Collection(session.Operations,
            item => AssertEnable(item, ApiProviders[0], TraceEventLevel.Informational, 0x10, [1, 2]),
            item => AssertEnable(item, ApiProviders[1], TraceEventLevel.Verbose, 2, [42, 43, 55, 56]),
            item => AssertEnable(item, ApiProviders[2], TraceEventLevel.Verbose, 2, [1, 2]));

        fixture.Source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42,
            Process.ProcessId, DateTime.FromFileTimeUtc((long)Process.ProcessStartKey));
        Assert.Equal(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, api.Read());
        Assert.Equal(3, session.Operations.Count);
        api.Dispose();
        Assert.Equal(1, session.Stops);
    }

    [Fact]
    public void FirstFrameAddsDisplayProvidersAndLastFrameRemovesThemOnTheSameSession()
    {
        using var fixture = new Fixture();
        using var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        var session = Assert.Single(fixture.Sessions);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        using var first = fixture.Source.AcquireSubscription();
        using var second = fixture.Source.AcquireSubscription();
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
        fixture.Clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1));
        Assert.True(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        first.Dispose();
        Assert.Equal(5, session.Operations.Count);
        second.Dispose();
        Assert.Equal(ApiProviders, session.Enabled);
        Assert.Equal(DisplayProviders.Select(provider => new Operation("disable", provider)),
            session.Operations.Skip(5));
        Assert.Equal(0, session.Stops);

        // A new frame observation cannot inherit the completed window from before the gap.
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        using var resumed = fixture.Source.AcquireSubscription();
        Assert.False(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        Assert.False(fixture.Source.ReadIntervals(fixture.Clock.GetUtcNow() - TimeSpan.FromSeconds(1),
            fixture.Clock.GetUtcNow())!.Complete);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1));
        Assert.True(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        Assert.Single(fixture.Sessions);
    }

    [Fact]
    public void FrameFirstThenApiKeepsTheFullProviderSetWithoutReenabling()
    {
        using var fixture = new Fixture();
        using var frame = fixture.Source.AcquireSubscription();
        var session = Assert.Single(fixture.Sessions);
        Assert.Equal(ApiProviders.Concat(DisplayProviders), session.Enabled);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        using var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        Assert.True(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        api.Dispose();
        Assert.Equal(5, session.Operations.Count);
        Assert.Equal(0, session.Stops);
        frame.Dispose();
        Assert.Equal(1, session.Stops);
        Assert.Single(fixture.Sessions);
    }

    [Fact]
    public void FrameWindowStartsAfterBothDisplayProvidersFinishEnabling()
    {
        using var fixture = new Fixture();
        using var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        var session = Assert.Single(fixture.Sessions);
        session.AfterEnable = _ => fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        using var frame = fixture.Source.AcquireSubscription();
        Assert.False(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1));
        Assert.True(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
        Assert.Single(fixture.Sessions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothLeaseDisposalOrdersStopOnlyAfterTheLastLease(bool frameLeavesFirst)
    {
        using var fixture = new Fixture();
        using var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        using var frame = fixture.Source.AcquireSubscription();
        var session = Assert.Single(fixture.Sessions);
        if (frameLeavesFirst) frame.Dispose(); else api.Dispose();
        Assert.Equal(0, session.Stops);
        Assert.Equal(frameLeavesFirst ? ApiProviders : ApiProviders.Concat(DisplayProviders), session.Enabled);
        if (frameLeavesFirst) api.Dispose(); else frame.Dispose();
        Assert.Equal(1, session.Stops);
        Assert.Null(fixture.Source.Read(TimeSpan.FromSeconds(1)));
        Assert.Single(fixture.Sessions);
        Assert.Equal(1, session.Stops);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void FreezeStopsAndThawDerivesProvidersFromTheSurvivingLeases(bool includeApi, bool includeFrames)
    {
        using var fixture = new Fixture();
        using var api = includeApi ? fixture.Source.AcquireGraphicsApiSubscription(Process) : null;
        using var frame = includeFrames ? fixture.Source.AcquireSubscription() : null;
        var session = Assert.Single(fixture.Sessions);
        fixture.Source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        Assert.Equal(1, session.Stops);
        Assert.Null(fixture.Source.Read(TimeSpan.FromSeconds(1)));
        Assert.Single(fixture.Sessions);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Source.ApplyMode(ResourceManagerComputeZoneMode.Normal);
        Assert.Equal(2, fixture.Sessions.Count);
        var resumed = fixture.Sessions[1];
        Assert.Equal(includeFrames ? ApiProviders.Concat(DisplayProviders) : ApiProviders, resumed.Enabled);
        if (includeFrames) Assert.False(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
    }

    [Fact]
    public void FrameAddedWhileFrozenIsEnabledOnlyWhenThawed()
    {
        using var fixture = new Fixture();
        fixture.Source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        using var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        using var frame = fixture.Source.AcquireSubscription();
        Assert.Empty(fixture.Sessions);
        fixture.Source.ApplyMode(ResourceManagerComputeZoneMode.Normal);
        Assert.Equal(ApiProviders.Concat(DisplayProviders), Assert.Single(fixture.Sessions).Enabled);
        Assert.False(fixture.Source.Read(TimeSpan.FromSeconds(1))!.Complete);
    }

    [Theory]
    [InlineData("enable", 0)]
    [InlineData("enable", 1)]
    [InlineData("disable", 0)]
    [InlineData("disable", 1)]
    public void FailedProviderTransitionRetiresTheSessionAndExistingDemandCanRecover(string kind, int index)
    {
        using var fixture = new Fixture();
        using var api = fixture.Source.AcquireGraphicsApiSubscription(Process);
        var session = Assert.Single(fixture.Sessions);
        using var frame = kind == "disable" ? fixture.Source.AcquireSubscription() : null;
        session.Fail = (kind, DisplayProviders[index]);
        using var added = kind == "enable" ? fixture.Source.AcquireSubscription() : null;
        if (kind == "disable") frame!.Dispose();
        Assert.Equal(1, session.Stops);
        _ = api.Read();
        Assert.Equal(2, fixture.Sessions.Count);
        Assert.Equal(kind == "enable" ? ApiProviders.Concat(DisplayProviders) : ApiProviders,
            fixture.Sessions[1].Enabled);
    }

    private static void AssertEnable(Operation item, Guid provider, TraceEventLevel level, ulong keywords, int[] ids)
    {
        Assert.Equal("enable", item.Kind);
        Assert.Equal(provider, item.Provider);
        Assert.Equal(level, item.Level);
        Assert.Equal(keywords, item.Keywords);
        Assert.Equal(ids, item.EventIds);
    }

    private sealed record Operation(string Kind, Guid Provider, TraceEventLevel Level = default,
        ulong Keywords = 0, int[]? EventIds = null);

    private sealed class RecordingSession : IPresentFrameEtwSession
    {
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Operation> Operations { get; } = [];
        public List<Guid> Enabled { get; } = [];
        public (string Kind, Guid Provider)? Fail { get; set; }
        public Action<Guid>? AfterEnable { get; set; }
        public int Stops { get; private set; }
        public int EventsLost => 0;
        public void Bind(Action<TraceEvent> handler) { }
        public void Process() => stopped.Task.GetAwaiter().GetResult();
        public void EnableProvider(Guid provider, TraceEventLevel level, ulong keywords, int[] eventIds)
        {
            Operations.Add(new("enable", provider, level, keywords, eventIds));
            ThrowIfRequested("enable", provider);
            Enabled.Add(provider);
            AfterEnable?.Invoke(provider);
        }
        public void DisableProvider(Guid provider)
        {
            Operations.Add(new("disable", provider));
            ThrowIfRequested("disable", provider);
            Enabled.Remove(provider);
        }
        private void ThrowIfRequested(string kind, Guid provider)
        {
            if (Fail == (kind, provider)) throw new InvalidOperationException("Recorded provider failure.");
        }
        public void Flush() { }
        public void Dispose() { Stops++; stopped.TrySetResult(); }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch.AddDays(1);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class Fixture : IDisposable
    {
        public Clock Clock { get; } = new();
        public List<RecordingSession> Sessions { get; } = [];
        public PresentFrameEtwSource Source { get; }
        public Fixture() => Source = new(NullLogger<PresentFrameEtwSource>.Instance, _ =>
        {
            var session = new RecordingSession();
            Sessions.Add(session);
            return session;
        }, Clock);
        public void Dispose() => Source.Dispose();
    }
}
