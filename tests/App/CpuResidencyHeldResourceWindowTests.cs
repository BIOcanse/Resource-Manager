using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Processes;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class CpuResidencyHeldResourceWindowTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FiniteWindowOpensStorageOnlyInsideTheWindowMethod()
    {
        var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new(), null, static (_, _) => { });
        owner.EnsureStarted();
        owner.BeginWindow(TimeSpan.FromSeconds(1));
        owner.PauseWindow();
        Assert.False(owner.IsWindowActive);
        Assert.Equal(2, broker.Acquires);
        Assert.Equal(1, broker.Releases);
        await owner.RunWindowAsync(() =>
        {
            Assert.True(owner.IsWindowActive);
            Assert.Equal(3, broker.Acquires);
            return Task.FromResult(true);
        });
        Assert.False(owner.IsWindowActive);
        Assert.Equal(2, broker.Releases);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cancellation")]
    [InlineData("exception")]
    [InlineData("publication")]
    public async Task NativeWindowMethodReturnsStorageAndDurationKeywordsOnEveryExit(string exit)
    {
        using var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new(), null, static (_, _) => { });
        owner.EnsureStarted();
        using var cancellation = new CancellationTokenSource();
        var failure = new IOException("window collection failed");
        var run = owner.RunWindowAsync(() =>
        {
            // The finite window starts exactly at the last processor's first known run.
            var start = checked(Math.Max(Stopwatch.GetTimestamp(), Floor(Session(owner))) + 1);
            NativeCpuReaderTestFeed.ClosedWindow(owner, [0, 1], 1000, firstTid: 10, startQpc: start);
            var held = owner.GetDiagnostics().Aggregation!.Value;
            Assert.True(held.WindowStorageBytes > 0);
            Assert.Equal(2, held.ActivePairCount);
            Assert.Equal(1, broker.DurationCount);
            if (exit == "cancellation")
            {
                cancellation.Cancel();
                return Task.FromCanceled<bool>(cancellation.Token);
            }
            if (exit == "exception") return Task.FromException<bool>(failure);
            if (exit == "skip") return Task.FromResult(false);
            Assert.True(owner.Collect());
            Assert.Equal(2000, Assert.Single(owner.Read()!.Processes).ExecutionTimeMilliseconds);
            return Task.FromResult(true);
        });
        if (exit == "cancellation")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else if (exit == "exception") Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => run));
        else Assert.Equal(exit == "publication", await run);
        // Acknowledge the queued pause at the existing consumer boundary.
        broker.Resource.Publish(broker.Resource.Tick);
        Assert.False(owner.IsWindowActive);
        Assert.Equal(0, broker.DurationCount);
        Assert.Equal(1, broker.ActiveCount);
        var idle = owner.GetDiagnostics().Aggregation!.Value;
        Assert.Equal(0, idle.ActivePairCount);
        Assert.Equal(0, idle.WindowStorageBytes);
        Assert.Equal(1, idle.CurrentProcessCount);
        Assert.Equal(2, idle.CurrentThreadCount);
        if (exit == "publication")
        {
            Assert.Equal(2000, Assert.Single(owner.Read()!.Processes).ExecutionTimeMilliseconds);
            Assert.Equal(1, owner.Revision);
        }
        else Assert.Null(owner.Read());
        owner.Stop();
        Assert.Equal(0, broker.ActiveCount);
        Assert.Equal(0UL, broker.Resource.RegionUsage.CurrentBytes);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cancellation")]
    [InlineData("exception")]
    [InlineData("publication")]
    public async Task ManagedOracleWindowMethodReleasesWindowStorageOnEveryExit(string exit)
    {
        using var broker = new OracleKernelEtwSessionBroker();
        OracleCpuResidencyCaptureOwner? owner = null;
        try
        {
            async Task Run()
            {
                await Task.Run(async () =>
                {
                    owner = new OracleCpuResidencyCaptureOwner(broker,
                        Configuration, new(), static (_, _) => { });
                    owner.EnsureStarted();
                    await owner.RunWindowAsync(() =>
                    {
                        var session = OracleSession(owner);
                        var buffer = (OracleCpuResidencyAggregationBuffer)session.GetType().GetProperty("Buffer")!.GetValue(session)!;
                        var at = DateTimeOffset.UtcNow;
                        var quantum = Stopwatch.Frequency / 20;
                        var start = (OracleFloor(session) / quantum + 1) * quantum;
                        buffer.ObserveProcessStart(start, at, 100, 11, 1000, "recorded");
                        buffer.ObserveThreadStart(start, at, 100, 10);
                        OracleFeed(owner, session, start, at, opening: true);
                        OracleFeed(owner, session, start + Stopwatch.Frequency / 2, at.AddMilliseconds(500), opening: false);
                        // A finite window (n = 0) holds pair records only while it measures.
                        Assert.Equal(0, buffer.GetDiagnostics().SlotsPerWindow);
                        Assert.True(buffer.GetDiagnostics().WindowStorageBytes > 0);
                        if (exit == "cancellation") return Task.FromCanceled<bool>(new CancellationToken(true));
                        if (exit == "exception") return Task.FromException<bool>(new IOException("window collection failed"));
                        if (exit == "skip") return Task.FromResult(false); // interrupted wait
                        OracleFeed(owner, session, start + Stopwatch.Frequency / 2, at.AddMilliseconds(500), opening: true);
                        OracleFeed(owner, session, start + Stopwatch.Frequency, at.AddSeconds(1), opening: false);
                        Assert.True(owner.Collect());
                        Assert.NotNull(owner.Read());
                        return Task.FromResult(true);
                    });
                });
            }
            if (exit == "cancellation") await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
            else if (exit == "exception") await Assert.ThrowsAsync<IOException>(Run);
            else await Run();
            Assert.NotNull(owner);
            Assert.False(owner.IsWindowActive);
            Assert.Equal(0, owner.GetDiagnostics().Aggregation!.Value.ActivePairCount);
            Assert.Equal(0, owner.GetDiagnostics().Aggregation!.Value.WindowStorageBytes);
            Assert.Equal(1, owner.GetDiagnostics().Aggregation!.Value.CurrentThreadCount);
            if (exit == "publication") Assert.NotNull(owner.Read());
        }
        finally { owner?.Dispose(); }
    }

    [Theory]
    [InlineData("publication")]
    [InlineData("stop")]
    public async Task ManagedOracleWindowExitsReleaseWindowStorageAndKeepPublishedValues(string exit)
    {
        await Task.Run(() =>
        {
            using var broker = new OracleKernelEtwSessionBroker();
            using var owner = new OracleCpuResidencyCaptureOwner(broker,
                Configuration,
                new(), static (_, _) => { });
            owner.EnsureStarted();
            // A one-second cadence over the one-second window: n = 1, whose first complete window is [S, S + W).
            owner.BeginWindow(TimeSpan.FromSeconds(1));
            var session = OracleSession(owner);
            var buffer = (OracleCpuResidencyAggregationBuffer)session.GetType().GetProperty("Buffer")!.GetValue(session)!;
            var at = DateTimeOffset.UtcNow;
            // Start on a 50 ms grid above the window floor; partial-slot
            // weighting belongs to the existing equivalence scenarios.
            var quantum = Stopwatch.Frequency / 20;
            var start = (OracleFloor(session) / quantum + 1) * quantum;
            buffer.ObserveProcessStart(start, at, 100, 11, 1000, "recorded");
            buffer.ObserveThreadStart(start, at, 100, 10);
            OracleFeed(owner, session, start, at, opening: true);
            var end = start + Stopwatch.Frequency;
            OracleFeed(owner, session, end, at.AddSeconds(1), opening: false);
            var trace = new List<object>();
            void Record(string phase) => trace.Add(new { phase, at = DateTimeOffset.UtcNow,
                qpc = Stopwatch.GetTimestamp(), diagnostics = buffer.GetDiagnostics() });
            Record("window");
            Assert.Equal(1, buffer.GetDiagnostics().SlotsPerWindow);
            Assert.True(buffer.GetDiagnostics().WindowStorageBytes > 0);
            Assert.Equal(2, buffer.GetDiagnostics().ActivePairCount);

            // The same synthetic input is projected before releasing any storage.
            var expected = EtwCpuResidencyCaptureOwner.BuildProcessSnapshot(Configuration(), owner.GetDiagnostics().SessionGeneration!.Value, buffer.Snapshot());
            Assert.True(owner.Collect());
            var published = Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot>(owner.Read());
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(published.Processes));
            Assert.Equal(2000, Assert.Single(published.Processes).ExecutionTimeMilliseconds);
            var content = JsonSerializer.Serialize(published);
            Record("publication");
            var continuity = buffer.GetDiagnostics().ContinuityEpoch;
            var lastReset = buffer.GetDiagnostics().LastCapacityReset;
            if (exit == "stop") owner.Stop();
            else owner.PauseWindow();
            Record("idle");
            Assert.False(owner.IsWindowActive);
            Assert.Equal(0, buffer.GetDiagnostics().WindowStorageBytes);
            Assert.Equal(0, buffer.GetDiagnostics().ActivePairCount);
            Assert.Equal(lastReset, buffer.GetDiagnostics().LastCapacityReset);
            Assert.Same(published, owner.Read());
            Assert.Equal(content, JsonSerializer.Serialize(published));
            if (exit != "stop")
            {
                Assert.Equal(1, buffer.GetDiagnostics().CurrentProcessCount);
                Assert.Equal(1, buffer.GetDiagnostics().CurrentThreadCount);
                // Releasing window storage is one continuity reset; identities stay.
                Assert.Equal(continuity + 1, buffer.GetDiagnostics().ContinuityEpoch);
                // Metadata remains writable during idle; published identities are copied.
                buffer.ObserveProcessStart(end + 1, at.AddSeconds(2), 100, 11, 1000, "renamed");
                owner.BeginWindow(TimeSpan.FromSeconds(1));
                Assert.Equal(0, buffer.GetDiagnostics().WindowStorageBytes); // Opening itself stays lazy.
                var nextStart = Math.Max(OracleFloor(session) + 1, end + 2);
                OracleFeed(owner, session, nextStart, at.AddSeconds(2), opening: true);
                OracleFeed(owner, session, nextStart + Stopwatch.Frequency, at.AddSeconds(3), opening: false);
                Record("next-window");
                Assert.True(buffer.GetDiagnostics().WindowStorageBytes > 0);
                Assert.Equal(2, buffer.GetDiagnostics().ActivePairCount);
                Assert.True(owner.Collect());
                Assert.Equal(content, JsonSerializer.Serialize(published));
                owner.PauseWindow();
                Record("next-idle");
                Assert.Equal(0, buffer.GetDiagnostics().WindowStorageBytes);
            }
            output.WriteLine(JsonSerializer.Serialize(new { exit, trace }));
        });
    }

    [Fact]
    public async Task CompletedOldWindowWaitCannotReportTheReplacementWindowReady()
    {
        using var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new());
        owner.EnsureStarted();
        owner.BeginWindow(TimeSpan.FromSeconds(1));
        var session = Session(owner);
        broker.Resource.Publish(broker.Resource.Tick);
        var start = Math.Max(System.Diagnostics.Stopwatch.GetTimestamp() + 1, Floor(session) + 1);
        var at = DateTimeOffset.UtcNow;
        var waiting = owner.WaitForCompleteWindowAsync(TimeSpan.FromSeconds(2), TimeSpan.Zero, CancellationToken.None);
        Feed(owner, session, start, at, opening: true);
        var gate = typeof(EtwCpuResidencyCaptureOwner).GetField("subscriptionGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        lock (gate)
        {
            // A ready continuation cannot pass the final window check until
            // this pause/replacement is visible under the same gate.
            Feed(owner, session, start + Stopwatch.Frequency, at.AddSeconds(1), opening: false);
            // The native wait must consume the old complete bank before its
            // final epoch check blocks on this gate. A reset before that read
            // would test a missing replacement window instead.
            Assert.True(SpinWait.SpinUntil(() => NativeWindowComplete(session), TimeSpan.FromSeconds(2)));
            Assert.False(waiting.IsCompleted);
            owner.PauseWindow();
            owner.BeginWindow(TimeSpan.FromSeconds(1));
        }
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await owner.WaitForCompleteWindowAsync(TimeSpan.FromMilliseconds(1), TimeSpan.Zero, CancellationToken.None));
        Assert.Equal(3, broker.Acquires);
        Assert.Equal(1, broker.Releases);
        Assert.Equal(1, broker.DurationCount);
        Assert.Equal(CpuResidencyHeldTestBroker.MetadataKeywords | KernelTraceEventParser.Keywords.ContextSwitch, broker.Keywords);
    }

    [Fact]
    public async Task RealtimeWindowWaitSeesBoundariesBeforeItsFirstWindowHasPassed()
    {
        // Production ordering: a realtime session floors the window at the reset
        // timestamp, so every readiness view in the first observation window starts
        // before that floor. Its boundary bit must still reach the waiter; otherwise
        // the wait ends at the boundary budget (one window) and never sees Current.
        using var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new(), null, static (_, _) => { });
        owner.EnsureStarted();
        typeof(NativeKernelEtwSession).GetField("realtime", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(broker.Resource.Session, true);
        owner.BeginWindow(TimeSpan.FromSeconds(1));
        var session = Session(owner);
        Assert.True(Floor(session) > 0);
        // Same budgets as the windowed reader: boundary budget = window (1 s), grace 2 s.
        var waiting = owner.WaitForCompleteWindowAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), CancellationToken.None);
        var start = Math.Max(Stopwatch.GetTimestamp() + 1, Floor(session) + 1);
        var at = DateTimeOffset.UtcNow;
        Feed(owner, session, start, at, opening: true);
        var opening = NativeAggregate(session);
        Assert.False(opening.IsComplete);
        // An incomplete view starts at S, which is never before the floor.
        Assert.True(opening.WindowStartQpc >= Floor(session));
        Assert.True(opening.HasBoundaries);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        Assert.False(waiting.IsCompleted);
        Feed(owner, session, start + Stopwatch.Frequency, at.AddSeconds(1), opening: false);
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        var closed = NativeAggregate(session);
        Assert.True(closed.IsComplete);
        Assert.True(closed.WindowStartQpc >= Floor(session));
    }

    [Fact]
    public void ResourceRebindPreservesThePausedWindowUntilItsCompositionOpensIt()
    {
        using var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new());
        owner.EnsureStarted();
        owner.BeginWindow(TimeSpan.FromSeconds(1));
        owner.PauseWindow();
        broker.Rebind();
        Assert.True(owner.HasSessionLease);
        Assert.False(owner.IsWindowActive);
        Assert.Equal(0, broker.DurationCount);
        Assert.Equal(CpuResidencyHeldTestBroker.MetadataKeywords, broker.Keywords);
        Assert.Equal(2, broker.Acquires);
        Assert.Equal(1, broker.Releases);
        owner.BeginWindow(TimeSpan.FromSeconds(1));
        Assert.True(owner.IsWindowActive);
        Assert.Equal(1, broker.DurationCount);
        Assert.Equal(CpuResidencyHeldTestBroker.MetadataKeywords | KernelTraceEventParser.Keywords.ContextSwitch, broker.Keywords);
        Assert.Equal(3, broker.Acquires);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshWindowRejectsOldCollectionAndDelayedBoundariesWithoutReplacingTheRunHolder(bool reopenBeforeCommit)
    {
        using var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new());
        owner.EnsureStarted();
        owner.BeginWindow(TimeSpan.FromSeconds(1));
        var session = Session(owner);
        var native = broker.Resource;
        broker.Resource.Publish(broker.Resource.Tick);
        var start = Math.Max(System.Diagnostics.Stopwatch.GetTimestamp() + 1, Floor(session) + 1);
        var at = DateTimeOffset.UtcNow;
        native.SeedProcess(100, 1000, "recorded");
        native.SeedThread(100, 1000, 10);
        Feed(owner, session, start, at, opening: true);
        var end = start + Stopwatch.Frequency;
        Feed(owner, session, end, at.AddSeconds(1), opening: false);
        Assert.True(owner.Collect());
        var first = Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot>(owner.Read());
        var revision = owner.Revision;
        var oldWindow = Window(session);
        var committed = false;
        Assert.False(owner.Collect(new BeforeCommit(() =>
        {
            owner.PauseWindow();
            Assert.False(owner.IsWindowActive);
            Observe(owner, session, end + 1, at.AddSeconds(1), 0, 0, 0, 100, 10);
            if (reopenBeforeCommit) owner.BeginWindow(TimeSpan.FromSeconds(1));
        }), (_, _) => committed = true));
        Assert.False(committed);
        Assert.Same(first, owner.Read());
        Assert.Equal(revision, owner.Revision);
        if (!reopenBeforeCommit) owner.BeginWindow(TimeSpan.FromSeconds(1));

        var nextWindow = Window(session);
        Assert.NotSame(oldWindow, nextWindow);
        Assert.True(owner.IsWindowActive);
        native.Publish(native.Tick);
        Assert.Equal(1, owner.GetDiagnostics().Aggregation!.Value.CurrentProcessCount);
        var continuity = owner.GetDiagnostics().Aggregation!.Value.ContinuityEpoch;
        // A delayed delivery from the paused/old window cannot reset the new
        // buffer, establish one of its boundaries, or complete its coverage.
        var staleQpc = end - 1;
        Assert.True(staleQpc > 0);
        Observe(owner, session, staleQpc, at, 0, 0, 0, 100, 10);
        Assert.Equal(continuity, owner.GetDiagnostics().Aggregation!.Value.ContinuityEpoch);
        Assert.Equal(1, owner.GetDiagnostics().Aggregation!.Value.CurrentProcessCount);
        Assert.False(NativeAggregate(session).HasBoundaries);
        Assert.False(NativeAggregate(session).IsComplete);
        Assert.True(owner.Collect());
        Assert.Null(owner.Read());

        native.Publish(native.Tick);
        var nextStart = Math.Max(Floor(session) + 1, end + 1);
        Feed(owner, session, nextStart, at.AddSeconds(2), opening: true);
        Assert.True(NativeAggregate(session).HasBoundaries);
        Assert.False(NativeAggregate(session).IsComplete);
        Assert.True(owner.Collect()); Assert.Null(owner.Read());

        Feed(owner, session, nextStart + Stopwatch.Frequency, at.AddSeconds(3), opening: false);
        Assert.True(NativeAggregate(session).IsComplete);

        Assert.True(owner.Collect());
        var next = Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot>(owner.Read());
        Assert.NotSame(first, next);
        Assert.Equal(TimeSpan.FromSeconds(1), next.Window);
        Assert.Equal(3, broker.Acquires);
        Assert.Equal(1, broker.Releases);
        owner.Stop();

        Assert.Equal(0UL, broker.Resource.RegionUsage.CurrentBytes);
        Assert.Equal(3, broker.Releases);
        Assert.Equal(0, broker.DurationCount);
        Assert.Equal(KernelTraceEventParser.Keywords.None, broker.Keywords);
    }

    private static CpuResidencyCaptureConfiguration Configuration() => new(1000, 1024,
        new Dictionary<int, CpuResidencyCoreAccounting>
        {
            [0] = new("core:0", "ccd:0", 1, 1), [1] = new("core:1", "ccd:1", 1, 1)
        }.ToFrozenDictionary(), "recorded held-resource window map");

    private static object Session(EtwCpuResidencyCaptureOwner owner)
        => typeof(EtwCpuResidencyCaptureOwner).GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static object OracleSession(OracleCpuResidencyCaptureOwner owner)
        => typeof(OracleCpuResidencyCaptureOwner).GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static long OracleFloor(object session)
        => (long)session.GetType().GetField("WindowStartQpc")!.GetValue(session)!;
    private static void OracleFeed(OracleCpuResidencyCaptureOwner owner, object session, long qpc, DateTimeOffset at, bool opening)
    {
        for (var processor = 0; processor < 2; processor++)
            typeof(OracleCpuResidencyCaptureOwner).GetMethod("ObserveContextSwitch", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(owner, [session, qpc, at, processor, opening ? 0 : 100, opening ? 0 : 10,
                    opening ? 100 : 0, null, opening ? 10 : 0]);
    }
    private static long Floor(object session)
    {
        var native = session.GetType().GetProperty("Native")!.GetValue(session)!;
        return (long)native.GetType().GetField("minimumWindowQpc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
    }
    private static object Window(object session)
        => session.GetType().GetProperty("Window")!.GetValue(session)!;
    private static bool NativeWindowComplete(object session)
    {
        var native = session.GetType().GetProperty("Native")!.GetValue(session)!;
        var gate = native.GetType().GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
        lock (gate)
            return ((CpuResidencyAggregationSnapshot)native.GetType().GetField("aggregate",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!).IsComplete;
    }
    private static CpuResidencyAggregationSnapshot NativeAggregate(object session)
    {
        var native = session.GetType().GetProperty("Native")!.GetValue(session)!;
        return (CpuResidencyAggregationSnapshot)native.GetType().GetMethod("ReadAggregate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(native, null)!;
    }
    private static void Feed(EtwCpuResidencyCaptureOwner owner, object session, long qpc, DateTimeOffset at, bool opening)
    {
        for (var processor = 0; processor < 2; processor++)
            Observe(owner, session, qpc, at, processor, opening ? 0 : 100, opening ? 0 : 10,
                opening ? 100 : 0, opening ? 10 : 0);
    }
    private static void Observe(EtwCpuResidencyCaptureOwner owner, object session, long qpc, DateTimeOffset at,
        int processor, int oldPid, int oldTid, int newPid, int newTid)
    {
        var native = NativeCpuReaderTestFeed.Resource(owner);
        native.CSwitch(qpc, at, processor, oldPid, oldTid, newPid, newTid, "recorded");
        native.Publish(native.Tick);
    }

    private sealed class BeforeCommit(Action before) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { before(); commit(); return true; }
    }

    private sealed class Broker : CpuResidencyHeldTestBroker { }
}
