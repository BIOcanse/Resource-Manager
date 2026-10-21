using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationSessionLifetimeTests
{
    [Fact]
    public void FailedStartupRetriesOnRequestedReadWithBoundedBackoff()
    {
        var factory = new Factory { Failures = 1 };
        var clock = new Clock();
        using var collector = Collector(factory, clock);
        using var lease = collector.AcquireSubscription();
        Assert.Null(collector.ReadCurrent(new Dictionary<int, long>()));
        Assert.Equal(1, factory.Attempts);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(collector.ReadCurrent(new Dictionary<int, long>()));
        Assert.Equal(2, factory.Attempts);
        Assert.True(Assert.Single(factory.Sessions).IsProcessing);
    }

    [Fact]
    public void UnexpectedExitReleasesOldRuntimeBeforeReplacementAndRejectsLateCallbacks()
    {
        var factory = new Factory();
        var clock = new Clock();
        using var collector = Collector(factory, clock);
        using var lease = collector.AcquireSubscription();
        collector.BeginRescan(1, 0); collector.CompleteRescan(1, 0);
        var first = collector.ReadCurrent(new Dictionary<int, long>())!;
        var old = Assert.Single(factory.Sessions);
        old.End(null);
        Assert.Null(collector.ReadCurrent(new Dictionary<int, long>()));
        Assert.Equal(1, factory.Attempts);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(collector.ReadCurrent(new Dictionary<int, long>()));
        Assert.Equal(new[] { "create:1", "stop:1", "create:2" }, factory.Lifecycle);
        collector.BeginRescan(2, 0); collector.CompleteRescan(2, 0);
        var replacement = collector.ReadCurrent(new Dictionary<int, long>())!;
        Assert.True(replacement.Generation > first.Generation);
        old.End(new IOException("late old callback"));
        Assert.Equal(replacement.Generation, collector.ReadCurrent(new Dictionary<int, long>())!.Generation);
    }

    [Fact]
    public void ReadDuringRestartBackoffDoesNotQueryAnEndedNativeSession()
    {
        var factory = new Factory();
        var clock = new Clock();
        using var collector = Collector(factory, clock);
        using var lease = collector.AcquireSubscription();
        var old = Assert.Single(factory.Sessions);
        old.RejectEndedSessionQueries = true;
        old.End(null);
        Assert.Null(collector.ReadCurrent(new Dictionary<int, long>()));
        Assert.Equal(1, factory.Attempts);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(collector.ReadCurrent(new Dictionary<int, long>()));
        Assert.Equal(2, factory.Attempts);
        Assert.Equal(1, old.StopCalls);
    }

    [Fact]
    public void UnconfirmedReleaseKeepsOwnershipAndCannotCreateASecondRuntime()
    {
        var factory = new Factory();
        var clock = new Clock();
        var collector = Collector(factory, clock);
        var lease = collector.AcquireSubscription();
        var old = Assert.Single(factory.Sessions);
        old.StopResult = new(true, false, true, ["fake disposal failure"]);
        old.End(new IOException("fake stream exit"));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(collector.ReadCurrent(new Dictionary<int, long>()));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(collector.ReadCurrent(new Dictionary<int, long>()));
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, old.StopCalls);
        lease.Dispose();
        using var nextLease = collector.AcquireSubscription();
        Assert.Equal(1, factory.Attempts);
        collector.Dispose();
    }

    [Fact]
    public void PartialFactoryCleanupFailureAlsoBlocksReplacementAfterLeaseRelease()
    {
        var factory = new Factory { PartialCleanupFailure = true };
        var clock = new Clock();
        using var collector = Collector(factory, clock);
        var lease = collector.AcquireSubscription();
        lease.Dispose();
        using var nextLease = collector.AcquireSubscription();
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(collector.ReadCurrent(new Dictionary<int, long>()));
        Assert.Equal(1, factory.Attempts);
    }

    [Fact]
    public void OnlyLastLeaseReleasesAndReadsAfterDisposalCannotRestart()
    {
        var factory = new Factory();
        var clock = new Clock();
        var collector = Collector(factory, clock);
        var first = collector.AcquireSubscription();
        var second = collector.AcquireSubscription();
        first.Dispose();
        Assert.Equal(0, factory.Sessions[0].StopCalls);
        second.Dispose();
        Assert.Equal(1, factory.Sessions[0].StopCalls);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(collector.ReadCurrent(new Dictionary<int, long>()));
        collector.Dispose();
        Assert.Null(collector.ReadCurrent(new Dictionary<int, long>()));
        Assert.Throws<ObjectDisposedException>(() => collector.AcquireSubscription());
        Assert.Equal(1, factory.Attempts);
    }

    private static GpuAllocationCollector Collector(Factory factory, Clock clock)
        => new(NullLogger<GpuAllocationCollector>.Instance, new ConfigurationBuilder().Build(), factory, clock);
    private sealed class Clock : TimeProvider
    {
        private long tick;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => tick;
        internal void Advance(TimeSpan interval) => tick += interval.Ticks;
    }
    private sealed class Factory : IGpuAllocationSessionFactory
    {
        internal int Attempts;
        internal int Failures;
        internal bool PartialCleanupFailure;
        internal List<Session> Sessions { get; } = [];
        internal List<string> Lifecycle { get; } = [];
        public IGpuAllocationSession Create(string name)
        {
            Attempts++;
            if (PartialCleanupFailure) throw new KernelEtwSessionCreationCleanupException(
                new IOException("fake creation failure"), new IOException("fake unconfirmed cleanup"));
            if (Failures-- > 0) throw new IOException("fake startup failure");
            var session = new Session(Attempts, Lifecycle);
            Sessions.Add(session); Lifecycle.Add("create:" + Attempts);
            return session;
        }
    }
    private sealed class Session(int id, List<string> lifecycle) : IGpuAllocationSession
    {
        private Action<Exception?>? completed;
        public bool IsProcessing { get; private set; }
        internal bool RejectEndedSessionQueries;
        public int EventsLost => RejectEndedSessionQueries && !IsProcessing
            ? throw new System.Runtime.InteropServices.COMException("Owned ended ETW session is no longer queryable.")
            : 0;
        internal int StopCalls;
        internal KernelEtwSessionStopResult StopResult = KernelEtwSessionStopResult.Released;
        public void Bind(Action<TraceEvent> handler) { }
        public void StartProcessing(Action<Exception?> completed) { this.completed = completed; IsProcessing = true; }
        public void EnableProvider(Guid provider, ulong keywords = 0, int[]? eventIds = null) { }
        public void CaptureState(Guid provider, ulong keywords) { }
        public void Flush() { }
        internal void End(Exception? failure) { IsProcessing = false; completed?.Invoke(failure); }
        public KernelEtwSessionStopResult Stop(TimeSpan timeout)
        { StopCalls++; lifecycle.Add("stop:" + id); IsProcessing = false; return StopResult; }
    }
}
