using System.Collections.Frozen;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class CpuResidencyHeldResourceWindowTests
{
    [Fact]
    public async Task CompletedOldWindowWaitCannotReportTheReplacementWindowReady()
    {
        var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new(), static (_, _) => { });
        owner.EnsureStarted();
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
        Assert.Equal(1, broker.Acquires);
        Assert.Equal(0, broker.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceRebindPreservesThePausedWindowUntilItsCompositionOpensIt(bool freeze)
    {
        var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new(), static (_, _) => { });
        owner.EnsureStarted();
        if (freeze) owner.SetCaptureEnabled(false); else owner.PauseWindow();
        broker.Rebind();
        Assert.True(owner.HasSessionLease);
        Assert.False(owner.IsWindowActive);
        Assert.Equal(1, broker.Acquires);
        Assert.Equal(0, broker.Releases);
        if (freeze) owner.SetCaptureEnabled(true); else owner.BeginWindow();
        Assert.True(owner.IsWindowActive);
        Assert.Equal(1, broker.Acquires);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshWindowRejectsOldCollectionAndDelayedBoundariesWithoutReplacingTheRunHolder(bool reopenBeforeCommit)
    {
        var broker = new Broker();
        using var owner = new EtwCpuResidencyCaptureOwner(broker, Configuration, new(), static (_, _) => { });
        owner.EnsureStarted();
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
        Assert.Equal(1, broker.Acquires);
        Assert.Equal(0, broker.Releases);
        owner.Stop();
        Observe(owner, session, nextStart + Stopwatch.Frequency + 1, at.AddSeconds(3), 0, 0, 0, 100, 10);
        Assert.Equal(0, buffer.GetDiagnostics().ActivePairCount);
        Assert.Equal(1, broker.Releases);
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
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            Acquires++;
            this.request = request;
            Bind();
            return new Lease(this);
        }
        internal void Rebind() { source!.Cancel(); generation++; Bind(); }
        private void Bind()
        {
            source = new KernelEtwEventSource();
            request!.Bind(source, new(generation, DateTimeOffset.UtcNow));
            source.Activate();
        }
        public KernelEtwSessionSnapshot GetSnapshot() => new("Running", "recorded", generation, DateTimeOffset.UnixEpoch,
            KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ContextSwitch,
            Acquires - Releases, 0);
        private sealed class Lease(Broker owner) : IKernelEtwSubscription
        {
            private bool released;
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose() { if (released) return; released = true; owner.source!.Cancel(); owner.Releases++; }
        }
    }
}
