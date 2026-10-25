using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationFrequencyTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(10, false)]
    [InlineData(30, false)]
    public async Task HighestHeldFrequencySelectsTheMechanismAndOnlyDuePublishes(int seconds, bool live)
    {
        var factory = new GpuAllocationTestSessionFactory();
        using var collector = factory.Collector(timeProvider: new NoTimers());
        using var lease = collector.AcquireSubscription(TimeSpan.FromSeconds(seconds));
        Assert.Empty(factory.Sessions);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var prior = collector.ReadCurrent(GpuAllocationTestSession.Processes)!;
        Assert.Equal(live, factory.Current.Enabled.Contains(GpuAllocationCollector.Provider));
        if (live)
        {
            factory.Current.Emit(80, ("hAllocationHandle", 11UL), ("hProcessId", 41U),
                ("ulSegmentId", 2U), ("Size", 2048UL), ("SegmentOffset", 0UL));
            Assert.Same(prior, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        }
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var next = collector.ReadCurrent(GpuAllocationTestSession.Processes)!;
        Assert.Equal(live ? 2048 : 1024, next.Adapters[23][41].PrivateBytes);
        Assert.Equal(1024, prior.Adapters[23][41].PrivateBytes);
        Assert.Equal(live ? 1 : 2, factory.Current.Captures);
        Assert.Equal(prior.Generation + 1, next.Generation);
        Assert.Equal(live, factory.Current.Enabled.Contains(GpuAllocationCollector.Provider));
        Assert.Single(factory.Sessions);
    }

    [Fact]
    public async Task BoundaryManifestEventDoesNotFailTheDueCapture()
    {
        var factory = new GpuAllocationTestSessionFactory();
        using var collector = factory.Collector(timeProvider: new NoTimers());
        using var lease = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);

        Assert.Equal(2, factory.Current.ManifestEvents);
        var value = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        Assert.NotNull(value);
        Assert.Equal(1024, value.Adapters[23][41].PrivateBytes);
    }

    [Fact]
    public async Task UiAdmissionAndReleaseSwitchOnlyAtNextDueAndAlwaysUseShortestHeldInterval()
    {
        var factory = new GpuAllocationTestSessionFactory();
        using var collector = factory.Collector(timeProvider: new NoTimers());
        using var smart = collector.AcquireSubscription(TimeSpan.FromSeconds(30));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Empty(factory.Current.Enabled);
        var ui = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        Assert.Empty(factory.Current.Enabled);
        Assert.Equal(1, factory.Current.Captures);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Contains(GpuAllocationCollector.Provider, factory.Current.Enabled);
        Assert.Equal(2, factory.Current.Captures);
        using var slow = collector.AcquireSubscription(TimeSpan.FromSeconds(60));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(2, factory.Current.Captures);
        ui.Dispose();
        Assert.Contains(GpuAllocationCollector.Provider, factory.Current.Enabled);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(3, factory.Current.Captures);
        Assert.Empty(factory.Current.Enabled);
        using var uiAgain = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        Assert.Empty(factory.Current.Enabled);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(4, factory.Current.Captures);
        Assert.Contains(GpuAllocationCollector.Provider, factory.Current.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveLossOrDecodeFailurePublishesEmptyThenNextDueRebuilds(bool malformed)
    {
        var log = new CaptureLog();
        var factory = new GpuAllocationTestSessionFactory();
        using var collector = factory.Collector(logger: log, timeProvider: new NoTimers());
        using var lease = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        var prior = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        if (malformed) factory.Current.Emit(35); else factory.Current.LoseEvent();
        Assert.Same(prior, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(1, factory.Current.Captures); // no rebuild in the failed round
        Assert.Empty(factory.Current.Enabled);
        Assert.Contains(log.Errors, error => error.Contains(malformed ? "decoding failed" : "events were lost"));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(2, factory.Current.Captures);
        Assert.Contains(GpuAllocationCollector.Provider, factory.Current.Enabled);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(2, factory.Current.Captures);
    }

    [Theory]
    [InlineData("0", 1, false)]
    [InlineData("2", 1, true)]
    [InlineData("2", 2, false)]
    [InlineData("1e100", 30, true)]
    public async Task VisibleConfigurationIsRespectedAndReadOnce(string threshold, int seconds, bool live)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [GpuAllocationCapturePlan.ThresholdKey] = threshold }).Build();
        var factory = new GpuAllocationTestSessionFactory();
        using var collector = factory.Collector(configuration: configuration, timeProvider: new NoTimers());
        using var lease = collector.AcquireSubscription(TimeSpan.FromSeconds(seconds));
        configuration[GpuAllocationCapturePlan.ThresholdKey] = live ? "0" : "1e100";
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Equal(live, factory.Current.Enabled.Contains(GpuAllocationCollector.Provider));
        Assert.Equal(live ? 1 : 2, factory.Current.Captures);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void InvalidThresholdFailsExplicitlyWithoutCreatingSession(string threshold)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [GpuAllocationCapturePlan.ThresholdKey] = threshold }).Build();
        var factory = new GpuAllocationTestSessionFactory();
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.Collector(configuration: configuration));
        Assert.Empty(factory.Sessions);
    }

    [Fact]
    public async Task ReleasingLastLiveLeaseStopsEventsAndCannotRepublishLateCallbacks()
    {
        var factory = new GpuAllocationTestSessionFactory();
        using var collector = factory.Collector(timeProvider: new NoTimers());
        var lease = collector.AcquireSubscription(TimeSpan.FromSeconds(1));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        lease.Dispose();
        Assert.Empty(factory.Current.Enabled);
        Assert.Equal(1, factory.Current.Stops);
        factory.Current.Emit(34, ("pDxgAdapter", 1UL), ("hVidMmGlobalAlloc", 2UL));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, default);
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(1, factory.Current.Captures);
    }

    private sealed class NoTimers : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new InvalidOperationException("Completed fake captures and frequency changes must not create a timer.");
    }
    private sealed class CaptureLog : ILogger<GpuAllocationCollector>
    {
        internal readonly List<string> Errors = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> format)
            => Errors.Add(format(state, exception) + " " + exception);
    }
}
