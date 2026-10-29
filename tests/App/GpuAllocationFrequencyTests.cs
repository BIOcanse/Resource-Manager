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
    public async Task DueCaptureCadenceSelectsTheMechanismAndOnlyDuePublishes(int seconds, bool live)
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness(timeProvider: new NoTimers());
        var collector = harness.Collector;
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(seconds), default);
        var prior = collector.ReadCurrent(GpuAllocationTestSession.Processes)!;
        Assert.Equal(live, factory.Current.Enabled.Contains(GpuAllocationCollector.Provider));
        if (live)
        {
            factory.Current.Emit(80, ("hAllocationHandle", 11UL), ("hProcessId", 41U),
                ("ulSegmentId", 2U), ("Size", 2048UL), ("SegmentOffset", 0UL));
            Assert.Same(prior, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        }
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(seconds), default);
        var next = collector.ReadCurrent(GpuAllocationTestSession.Processes)!;
        Assert.Equal(live ? 2048 : 1024, next.Adapters[23][41].PrivateBytes);
        Assert.Equal(1024, prior.Adapters[23][41].PrivateBytes);
        Assert.Equal(live ? 1 : 2, factory.Sessions.Sum(session => session.Captures));
        Assert.Equal(prior.Generation + 1, next.Generation);
        Assert.Equal(live, factory.Current.Enabled.Contains(GpuAllocationCollector.Provider));
        Assert.Equal(live ? 1 : 2, factory.Sessions.Count);
    }

    [Fact]
    public async Task BoundaryManifestEventDoesNotFailTheDueCapture()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness(timeProvider: new NoTimers());
        var collector = harness.Collector;
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        Assert.Empty(factory.Sessions); // A ready block owns no native session before its first due capture.
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        var first = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        Assert.NotNull(first);
        Assert.Equal(1024, first.Adapters[23][41].PrivateBytes);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);

        Assert.Equal(4, factory.Sessions.Sum(session => session.ManifestEvents)); // Each finite session enables startup and scan boundary events.
        Assert.Equal(2, factory.Sessions.Sum(session => session.Captures));
        Assert.All(factory.Sessions, session => { Assert.Equal(1, session.Stops); Assert.True(session.Joined); });
        var value = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        Assert.NotNull(value);
        Assert.Equal(1024, value.Adapters[23][41].PrivateBytes);
    }

    [Fact]
    public async Task CadenceChangesReleaseLiveHoldsBeforeTheNextDueCapture()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness(timeProvider: new NoTimers());
        var collector = harness.Collector;
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.Empty(factory.Current.Enabled);
        Assert.Equal(1, factory.Current.Captures);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        Assert.Contains(GpuAllocationCollector.Provider, factory.Current.Enabled);
        Assert.Equal(2, factory.Sessions.Sum(session => session.Captures));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        Assert.Equal(2, factory.Sessions.Sum(session => session.Captures));
        Assert.Contains(GpuAllocationCollector.Provider, factory.Current.Enabled);
        var prior = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        collector.SynchronizeCaptureInterval(TimeSpan.FromSeconds(30));
        Assert.Equal(1, factory.Current.Stops);
        Assert.True(factory.Current.Joined);
        Assert.Same(prior, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
        Assert.Equal(3, factory.Sessions.Sum(session => session.Captures));
        Assert.Empty(factory.Current.Enabled);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        Assert.Equal(4, factory.Sessions.Sum(session => session.Captures));
        Assert.Equal(4, factory.Attempts);
        Assert.Contains(GpuAllocationCollector.Provider, factory.Current.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveLossOrDecodeFailurePublishesEmptyThenNextDueRebuilds(bool malformed)
    {
        var log = new CaptureLog();
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness(logger: log, timeProvider: new NoTimers());
        var collector = harness.Collector;
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        var prior = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        if (malformed) factory.Current.Emit(35); else factory.Current.LoseEvent();
        Assert.Same(prior, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        Assert.Null(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(1, factory.Current.Captures); // no rebuild in the failed round
        Assert.Empty(factory.Current.Enabled);
        Assert.Contains(log.Errors, error => error.Contains(malformed ? "decoding failed" : "events were lost"));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        Assert.NotNull(collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(2, factory.Sessions.Sum(session => session.Captures));
        Assert.Contains(GpuAllocationCollector.Provider, factory.Current.Enabled);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        Assert.Equal(2, factory.Sessions.Sum(session => session.Captures));
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
        await using var harness = factory.Harness(configuration: configuration, timeProvider: new NoTimers());
        var collector = harness.Collector;
        await harness.SetInUseAsync(true);
        configuration[GpuAllocationCapturePlan.ThresholdKey] = live ? "0" : "1e100";
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(seconds), default);
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(seconds), default);
        Assert.Equal(live, factory.Current.Enabled.Contains(GpuAllocationCollector.Provider));
        Assert.Equal(live ? 1 : 2, factory.Sessions.Sum(session => session.Captures));
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
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.Harness(configuration: configuration));
        Assert.Empty(factory.Sessions);
    }

    [Fact]
    public async Task StoppingTheLiveProducerRetainsPublishedValueUntilTheNextDuePublishesEmpty()
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness(timeProvider: new NoTimers());
        var collector = harness.Collector;
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        var published = collector.ReadCurrent(GpuAllocationTestSession.Processes);
        await harness.StopAsync();
        Assert.Equal(1, factory.Current.Stops);
        Assert.Equal(Timeout.InfiniteTimeSpan, factory.Current.StopTimeout);
        Assert.Empty(factory.Current.Enabled);
        Assert.False(factory.Current.HasBoundHandler);
        Assert.Same(published, collector.ReadCurrent(GpuAllocationTestSession.Processes));
        factory.Current.Emit(34, ("pDxgAdapter", 1UL), ("hVidMmGlobalAlloc", 2UL));
        await collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(30), default);
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
