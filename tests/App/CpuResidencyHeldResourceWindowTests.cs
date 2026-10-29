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
    public async Task FiniteResumeOpensStorageOnlyInsideTheWindowMethod()
    {
        var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new(), static (_, _) => { });
        owner.EnsureStarted();
        owner.BeginWindow();
        owner.SetCaptureEnabled(false);
        owner.SetCaptureEnabled(true, beginWindow: false);
        Assert.False(owner.IsWindowActive);
        Assert.Equal(2, broker.Acquires);
        Assert.Equal(1, broker.Releases);
        using var storage = new CaptureBucketStorage();
        await owner.RunWindowAsync(storage, () =>
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
    public async Task WindowMethodReturnsRealLedgerRegionsOnEveryExit(string exit)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var owner = new EtwCpuResidencyCaptureOwner(new Broker(),
            () => Configuration() with { BucketWidthMilliseconds = 1 }, new(), static (_, _) => { });
        owner.EnsureStarted();
        async Task Run()
        {
            await LedgerCaptureBucketStorage.RunAsync(ledger, async allocate =>
            {
                using var storage = new CaptureBucketStorage(allocate);
                await owner.RunWindowAsync(storage, () =>
                {
                    var session = Session(owner);
                    var buffer = (CpuResidencyAggregationBuffer)session.GetType().GetProperty("Buffer")!.GetValue(session)!;
                    var at = DateTimeOffset.UtcNow;
                    var quantum = Stopwatch.Frequency / 20;
                    var start = (Floor(session) / quantum + 1) * quantum;
                    buffer.ObserveProcessStart(start, at, 100, 11, 1000, "recorded");
                    buffer.ObserveThreadStart(start, at, 100, 10);
                    Feed(owner, session, start, at, opening: true);
                    Feed(owner, session, start + Stopwatch.Frequency / 2, at.AddMilliseconds(500), opening: false);
                    var held = ledger.ReadCaptureBucketDiagnostics();
                    Assert.True(held.AcquiredLeaseCount > 0);
                    Assert.True(held.CommittedBucketBytes > 0);
                    Assert.True(ledger.ReadTable().SystemPageBytes > 0);
                    if (exit == "cancellation") return Task.FromCanceled<bool>(new CancellationToken(true));
                    if (exit == "exception") return Task.FromException<bool>(new IOException("window collection failed"));
                    if (exit == "skip") return Task.FromResult(false); // interrupted wait
                    Feed(owner, session, start + Stopwatch.Frequency / 2, at.AddMilliseconds(500), opening: true);
                    Feed(owner, session, start + Stopwatch.Frequency, at.AddSeconds(1), opening: false);
                    Assert.True(owner.Collect());
                    Assert.NotNull(owner.Read());
                    return Task.FromResult(true);
                });
            });
        }
        if (exit == "cancellation") await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else if (exit == "exception") await Assert.ThrowsAsync<IOException>(Run);
        else await Run();
        var returned = ledger.ReadCaptureBucketDiagnostics();
        Assert.Equal(0, returned.AcquiredLeaseCount);
        Assert.Equal(0UL, returned.CommittedBucketBytes);
        Assert.Equal(0UL, ledger.ReadTable().SystemPageBytes);
        Assert.False(owner.IsWindowActive);
        Assert.Equal(0, owner.GetDiagnostics().Aggregation!.Value.ActivePairCount);
        Assert.Equal(0, owner.GetDiagnostics().Aggregation!.Value.RetainedBucketBytes);
        Assert.Equal(1, owner.GetDiagnostics().Aggregation!.Value.CurrentThreadCount);
        if (exit == "publication") Assert.NotNull(owner.Read());
    }

    [Theory]
    [InlineData("publication", false)]
    [InlineData("publication", true)]
    [InlineData("freeze", false)]
    [InlineData("freeze", true)]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    public async Task WindowExitsReturnEveryBucketAndKeepPublishedValues(string exit, bool ledgerBacked)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        await LedgerCaptureBucketStorage.RunAsync(ledger, allocate =>
        {
            var allocations = new List<CountedBucket>();
            var broker = new Broker();
            using var owner = new EtwCpuResidencyCaptureOwner(broker,
                () => Configuration() with { BucketWidthMilliseconds = ledgerBacked ? 1 : 50 },
                new(), static (_, _) => { }, allocateBuckets: length =>
                {
                    var acquiredBefore = ledger.ReadCaptureBucketDiagnostics().AcquiredLeaseCount;
                    var memory = ledgerBacked ? allocate(length)
                        : CaptureBucketMemory.AllocateManaged(length);
                    if (ledgerBacked) Assert.Equal(acquiredBefore + 1, ledger.ReadCaptureBucketDiagnostics().AcquiredLeaseCount);
                    var counted = new CountedBucket(memory);
                    allocations.Add(counted);
                    return counted;
                });
            owner.EnsureStarted();
            owner.BeginWindow();
            var session = Session(owner);
            var buffer = (CpuResidencyAggregationBuffer)session.GetType().GetProperty("Buffer")!.GetValue(session)!;
            var at = DateTimeOffset.UtcNow;
            // Align the exact-duration fixture to both bucket widths; partial-edge
            // weighting belongs to the existing equivalence scenarios.
            var quantum = Stopwatch.Frequency / 20;
            var start = (Floor(session) / quantum + 1) * quantum;
            buffer.ObserveProcessStart(start, at, 100, 11, 1000, "recorded");
            buffer.ObserveThreadStart(start, at, 100, 10);
            Feed(owner, session, start, at, opening: true);
            var end = start + Stopwatch.Frequency;
            Feed(owner, session, end, at.AddSeconds(1), opening: false);
            var trace = new List<object>();
            void Record(string phase) => trace.Add(new { phase, at = DateTimeOffset.UtcNow,
                qpc = Stopwatch.GetTimestamp(), diagnostics = buffer.GetDiagnostics(),
                allocated = allocations.Count, disposed = allocations.Count(value => value.Disposals == 1) });
            Record("window");
            Assert.True(buffer.GetDiagnostics().RetainedBucketBytes > 0);
            Assert.Equal(2, buffer.GetDiagnostics().ActivePairCount);
            if (ledgerBacked) Assert.True(ledger.ReadTable().RegisteredBytes > 0);

            // The same synthetic input is projected before releasing any storage.
            var expected = EtwCpuResidencyCaptureOwner.BuildProcessSnapshot(Configuration(), 7, buffer.Snapshot([0, 1]));
            Assert.True(owner.Collect());
            var published = Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot>(owner.Read());
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(published.Processes));
            Assert.Equal(2000, Assert.Single(published.Processes).ExecutionTimeMilliseconds);
            var content = JsonSerializer.Serialize(published);
            Record("publication");
            var continuity = buffer.GetDiagnostics().ContinuityEpoch;
            var lastReset = buffer.GetDiagnostics().LastCapacityReset;
            var firstAllocations = allocations.Count;
            if (exit == "freeze") owner.SetCaptureEnabled(false);
            else if (exit == "stop") owner.Stop();
            else owner.PauseWindow();
            Record("idle");
            Assert.False(owner.IsWindowActive);
            Assert.Equal(0, buffer.GetDiagnostics().RetainedBucketBytes);
            Assert.Equal(0, buffer.GetDiagnostics().ActivePairCount);
            Assert.Equal(lastReset, buffer.GetDiagnostics().LastCapacityReset);
            Assert.All(allocations, value => Assert.Equal(1, value.Disposals));
            Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
            Assert.Same(published, owner.Read());
            Assert.Equal(content, JsonSerializer.Serialize(published));
            if (exit != "stop")
            {
                Assert.Equal(1, buffer.GetDiagnostics().CurrentProcessCount);
                Assert.Equal(1, buffer.GetDiagnostics().CurrentThreadCount);
                Assert.Equal(continuity, buffer.GetDiagnostics().ContinuityEpoch);
                // Metadata remains writable during idle; published identities are copied.
                buffer.ObserveProcessStart(end + 1, at.AddSeconds(2), 100, 11, 1000, "renamed");
                if (exit == "freeze") owner.SetCaptureEnabled(true); else owner.BeginWindow();
                Assert.Equal(firstAllocations, allocations.Count); // Opening itself stays lazy.
                var nextStart = Math.Max(Floor(session) + 1, end + 2);
                Feed(owner, session, nextStart, at.AddSeconds(2), opening: true);
                Feed(owner, session, nextStart + Stopwatch.Frequency, at.AddSeconds(3), opening: false);
                Record("next-window");
                Assert.True(buffer.GetDiagnostics().RetainedBucketBytes > 0);
                Assert.Equal(2, buffer.GetDiagnostics().ActivePairCount);
                Assert.True(allocations.Count > firstAllocations);
                Assert.True(owner.Collect());
                Assert.Equal(content, JsonSerializer.Serialize(published));
                owner.PauseWindow();
                Record("next-idle");
                Assert.Equal(0, buffer.GetDiagnostics().RetainedBucketBytes);
                Assert.All(allocations, value => Assert.Equal(1, value.Disposals));
                Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
            }
            output.WriteLine(JsonSerializer.Serialize(new { exit, ledgerBacked, trace }));
            return Task.CompletedTask;
        });
    }

    private sealed class CountedBucket(CaptureBucketMemory inner) : CaptureBucketMemory
    {
        internal int Disposals;
        internal bool FailNextClose;
        internal override Span<byte> Bytes => inner.Bytes;
        public override void Dispose()
        {
            if (FailNextClose) { FailNextClose = false; throw new IOException("recorded close failure"); }
            inner.Dispose(); Disposals++;
        }
    }

    [Fact]
    public async Task PausedWindowAlsoReturnsPendingLedgerResizeStorage()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        await LedgerCaptureBucketStorage.RunAsync(ledger, allocate =>
        {
            var allocations = new List<CountedBucket>();
            var buffer = new CpuResidencyAggregationBuffer(1000, 1000, 8, 1, length =>
            {
                var acquiredBefore = ledger.ReadCaptureBucketDiagnostics().AcquiredLeaseCount;
                var memory = allocate(length);
                Assert.Equal(acquiredBefore + 1, ledger.ReadCaptureBucketDiagnostics().AcquiredLeaseCount);
                var counted = new CountedBucket(memory) { FailNextClose = true };
                allocations.Add(counted);
                return counted;
            });
            var at = DateTimeOffset.UnixEpoch;
            buffer.SeedProcess(1, 1, "recorded");
            buffer.SeedThread(1, 1);
            void Switch(long tick, int core, int old, int next)
                => buffer.ObserveContextSwitch(tick, at.AddMilliseconds(tick), core, old, old, next, null, next);
            Switch(0, 0, 0, 1);
            Switch(0, 1, 0, 0);
            Switch(1000, 1, 0, 0);
            Assert.Throws<IOException>(() => Switch(2000, 0, 1, 0));
            Assert.Equal(2, allocations.Count);
            Assert.True(ledger.ReadTable().RegisteredBytes > 0);
            buffer.ReleaseWindowBuckets();
            Assert.All(allocations, value => Assert.Equal(1, value.Disposals));
            Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
            Assert.Equal(0, buffer.GetDiagnostics().ActivePairCount);
            Assert.Equal(0, buffer.GetDiagnostics().RetainedBucketBytes);
            Assert.Equal(1, buffer.GetDiagnostics().CurrentThreadCount);
            return Task.CompletedTask;
        });
    }
    [Fact]
    public async Task CompletedOldWindowWaitCannotReportTheReplacementWindowReady()
    {
        var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new(), static (_, _) => { });
        owner.EnsureStarted();
        owner.BeginWindow();
        var session = Session(owner);
        var start = Floor(session) + 1;
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
            owner.PauseWindow();
            owner.BeginWindow();
        }
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(Window(session).Complete.IsCompleted);
        Assert.Equal(3, broker.Acquires);
        Assert.Equal(1, broker.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceRebindPreservesThePausedWindowUntilItsCompositionOpensIt(bool freeze)
    {
        var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new(), static (_, _) => { });
        owner.EnsureStarted();
        owner.BeginWindow();
        if (freeze) owner.SetCaptureEnabled(false); else owner.PauseWindow();
        broker.Rebind();
        Assert.True(owner.HasSessionLease);
        Assert.False(owner.IsWindowActive);
        Assert.Equal(2, broker.Acquires);
        Assert.Equal(1, broker.Releases);
        if (freeze) owner.SetCaptureEnabled(true); else owner.BeginWindow();
        Assert.True(owner.IsWindowActive);
        Assert.Equal(3, broker.Acquires);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshWindowRejectsOldCollectionAndDelayedBoundariesWithoutReplacingTheRunHolder(bool reopenBeforeCommit)
    {
        var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new(), static (_, _) => { });
        owner.EnsureStarted();
        owner.BeginWindow();
        var session = Session(owner);
        var buffer = (CpuResidencyAggregationBuffer)session.GetType().GetProperty("Buffer")!.GetValue(session)!;
        var start = Floor(session) + 1;
        var at = DateTimeOffset.UtcNow;
        buffer.ObserveProcessStart(start, at, 100, 11, 1000, "recorded");
        buffer.ObserveThreadStart(start, at, 100, 10);
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
            if (reopenBeforeCommit) owner.BeginWindow();
        }), (_, _) => committed = true));
        Assert.False(committed);
        Assert.Same(first, owner.Read());
        Assert.Equal(revision, owner.Revision);
        if (!reopenBeforeCommit) owner.BeginWindow();

        var nextWindow = Window(session);
        Assert.NotSame(oldWindow, nextWindow);
        Assert.True(owner.IsWindowActive);
        Assert.Equal(1, buffer.GetDiagnostics().CurrentProcessCount);
        var continuity = buffer.GetDiagnostics().ContinuityEpoch;
        // A delayed delivery from the paused/old window cannot reset the new
        // buffer, establish one of its boundaries, or complete its coverage.
        Observe(owner, session, Floor(session) - 1, at, 0, 0, 0, 100, 10);
        Assert.Equal(continuity, buffer.GetDiagnostics().ContinuityEpoch);
        Assert.False(nextWindow.Boundaries.IsCompleted);
        Assert.False(nextWindow.Complete.IsCompleted);
        Assert.True(owner.Collect());
        Assert.Null(owner.Read());

        var nextStart = Math.Max(Floor(session) + 1, end + 1);
        Feed(owner, session, nextStart, at.AddSeconds(2), opening: true);
        Assert.True(nextWindow.Boundaries.IsCompletedSuccessfully);
        Assert.False(nextWindow.Complete.IsCompleted);
        Feed(owner, session, nextStart + Stopwatch.Frequency, at.AddSeconds(3), opening: false);
        Assert.True(nextWindow.Complete.IsCompletedSuccessfully);
        Assert.True(owner.Collect());
        var next = Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot>(owner.Read());
        Assert.NotSame(first, next);
        Assert.Equal(TimeSpan.FromSeconds(1), next.Window);
        Assert.Equal(3, broker.Acquires);
        Assert.Equal(1, broker.Releases);
        owner.Stop();
        Observe(owner, session, nextStart + Stopwatch.Frequency + 1, at.AddSeconds(3), 0, 0, 0, 100, 10);
        Assert.Equal(0, buffer.GetDiagnostics().ActivePairCount);
        Assert.Equal(3, broker.Releases);
    }

    private static CpuResidencyCaptureConfiguration Configuration() => new(1000, 1024,
        new Dictionary<int, CpuResidencyCoreAccounting>
        {
            [0] = new("core:0", "ccd:0", 1, 1), [1] = new("core:1", "ccd:1", 1, 1)
        }.ToFrozenDictionary(), "recorded held-resource window map");

    private static object Session(EtwCpuResidencyCaptureOwner owner)
        => typeof(EtwCpuResidencyCaptureOwner).GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static long Floor(object session) => (long)session.GetType().GetField("WindowStartQpc")!.GetValue(session)!;
    private static CpuResidencyWindowCompletion Window(object session)
        => (CpuResidencyWindowCompletion)session.GetType().GetProperty("Window")!.GetValue(session)!;
    private static void Feed(EtwCpuResidencyCaptureOwner owner, object session, long qpc, DateTimeOffset at, bool opening)
    {
        for (var processor = 0; processor < 2; processor++)
            Observe(owner, session, qpc, at, processor, opening ? 0 : 100, opening ? 0 : 10,
                opening ? 100 : 0, opening ? 10 : 0);
    }
    private static void Observe(EtwCpuResidencyCaptureOwner owner, object session, long qpc, DateTimeOffset at,
        int processor, int oldPid, int oldTid, int newPid, int newTid)
        => typeof(EtwCpuResidencyCaptureOwner).GetMethod("ObserveContextSwitch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, [session, qpc, at, processor, oldPid, oldTid, newPid, "recorded", newTid]);

    private sealed class BeforeCommit(Action before) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { before(); commit(); return true; }
    }

    private sealed class Broker : IKernelEtwSessionBroker
    {
        internal int Acquires, Releases;
        private int generation = 7;
        private KernelEtwSubscriptionRequest? request;
        private KernelEtwEventSource? source;
        private readonly List<Lease> leases = [];
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            Acquires++;
            var metadata = request.Keywords.HasFlag(KernelTraceEventParser.Keywords.Process);
            var lease = new Lease(this, request.Keywords, metadata);
            leases.Add(lease);
            if (metadata) { this.request = request; Bind(); }
            else request.Bind(source!, new(generation, DateTimeOffset.UtcNow));
            return lease;
        }
        internal void Rebind() { source!.Cancel(); generation++; Bind(); }
        private void Bind()
        {
            source = new KernelEtwEventSource();
            request!.Bind(source, new(generation, DateTimeOffset.UtcNow));
            source.Activate();
        }
        public KernelEtwSessionSnapshot GetSnapshot() => new("Running", "recorded", generation, DateTimeOffset.UnixEpoch,
            leases.Aggregate(KernelTraceEventParser.Keywords.None, (keywords, lease) => keywords | lease.Keywords),
            Acquires - Releases, 0);
        private sealed class Lease(Broker owner, KernelTraceEventParser.Keywords keywords, bool metadata) : IKernelEtwSubscription
        {
            internal KernelTraceEventParser.Keywords Keywords { get; } = keywords;
            private bool released;
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose()
            {
                if (released) return;
                released = true;
                owner.leases.Remove(this);
                if (metadata) owner.source!.Cancel();
                owner.Releases++;
            }
        }
    }
}
