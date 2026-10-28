using System.Diagnostics;
using System.Reflection;
using ResourceManager.Subscription.Native;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwCpuResidencyWindowDemandTests
{
    private const KernelTraceEventParser.Keywords HeldKeywords = KernelTraceEventParser.Keywords.Process
        | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ContextSwitch;

    [Fact]
    public async Task StoppedBrokerPublishesAnEmptyWindowWithoutEndingTheSamplingWorker()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        broker.Stop();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using var output = reader.SubscribeAsync("test.stopped-broker", TimeSpan.FromSeconds(30), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var first = output.MoveNextAsync().AsTask();
        await reader.StartAsync(cancellation.Token);
        try
        {
            var completed = await Task.WhenAny(first, reader.ExecuteTask!).WaitAsync(TimeSpan.FromSeconds(9));
            Assert.Same(first, completed);
            Assert.True(await first);
            Assert.Equal("cpu-residency-session-unavailable",
                Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencyUnavailable>(output.Current).ReasonCode);
            Assert.False(reader.ExecuteTask!.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            await reader.StopAsync(CancellationToken.None);
            try { await first; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task MissingBoundaryDataPublishesEmptyOnceAndDoesNotRetryBeforeTheNextDue()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using var output = reader.SubscribeAsync("test.empty", TimeSpan.FromSeconds(30), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var first = output.MoveNextAsync().AsTask();
        await reader.StartAsync(cancellation.Token);
        try
        {
            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(9)));
            Assert.Equal("cpu-residency-window-warming",
                Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencyUnavailable>(output.Current).ReasonCode);
            Assert.Equal(1, reader.CanonicalRevision);
            await UntilAsync(() => !IsWindowActive(reader));
            await Task.Delay(150, cancellation.Token);
            Assert.Equal(HeldKeywords, broker.Keywords);
            Assert.Equal(1, broker.SubscribeCount);
            Assert.Equal(1, broker.ActiveCount);
            Assert.Equal(1, reader.CanonicalRevision);
        }
        finally { cancellation.Cancel(); await reader.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task FreezePausesWindowAndRetainsTheRunLeaseWithoutErasingCurrent()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        using var demand = reader.AcquireSubscription("test.freeze", TimeSpan.FromSeconds(30));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => IsWindowActive(reader));
            FeedWindow(reader, sampling);
            await UntilAsync(() => reader.Read() is not null);
            var current = reader.Read();
            reader.ApplyMode(ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Freeze);
            await UntilAsync(() => !IsWindowActive(reader));
            await Task.Delay(150);
            Assert.Equal(HeldKeywords, broker.Keywords);
            Assert.Equal(1, broker.SubscribeCount);
            Assert.Equal(1, broker.ActiveCount);
            Assert.Equal(1, broker.SessionStarts);
            Assert.Same(current, reader.Read());
        }
        finally { await reader.StopAsync(CancellationToken.None); }
        Assert.Equal(KernelTraceEventParser.Keywords.None, broker.Keywords);
        Assert.Equal(0, broker.ActiveCount);
    }

    [Fact]
    public async Task ThirtySecondDemandPublishesOneWindowPerDueWhileHoldingTheRunLeaseBetweenWindows()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var output = reader.SubscribeAsync("test.thirty-seconds", TimeSpan.FromSeconds(30), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var first = output.MoveNextAsync().AsTask();
        await reader.StartAsync(cancellation.Token);
        try
        {
            await UntilAsync(() => IsWindowActive(reader));
            var previousWindowEnd = FeedWindow(reader, sampling);
            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            var current = Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot>(output.Current);
            await UntilAsync(() => !IsWindowActive(reader));
            Assert.Equal(HeldKeywords, broker.Keywords);
            Assert.Equal(1, broker.SubscribeCount);
            Assert.Equal(1, broker.ActiveCount);
            Assert.Equal(1, reader.CanonicalRevision);
            var second = output.MoveNextAsync().AsTask();
            await Task.Delay(150, cancellation.Token);
            Assert.False(second.IsCompleted);
            Assert.Same(current, reader.Read());
            await UntilAsync(() => IsWindowActive(reader), TimeSpan.FromSeconds(35));
            Assert.False(second.IsCompleted);
            FeedWindow(reader, sampling, previousWindowEnd);
            Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(35)));
            Assert.Equal(2, reader.CanonicalRevision);
            Assert.NotSame(current, reader.Read());
            Assert.Equal(HeldKeywords, broker.Keywords);
            Assert.Equal(1, broker.SubscribeCount);
            Assert.Equal(1, broker.ActiveCount);
            Assert.Equal(1, broker.SessionStarts);
            Assert.All(broker.Generations, generation => Assert.Equal(7, generation));
        }
        finally { cancellation.Cancel(); await reader.StopAsync(CancellationToken.None); }
        Assert.Equal(KernelTraceEventParser.Keywords.None, broker.Keywords);
        Assert.NotNull(reader.Read()); // Stopping demand does not erase current.
    }

    [Fact]
    public async Task FasterSubscriptionKeepsCaptureContinuousAndLastWithdrawalNeedsExplicitWorkerStop()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        var slowInterval = TimeSpan.FromSeconds(30);
        using var slow = reader.AcquireSubscription("test.slow", slowInterval);
        using var fast = reader.AcquireSubscription("test.fast", TimeSpan.FromMilliseconds(100));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => IsWindowActive(reader));
            var capture = CaptureOwner(reader)!;
            var initialSession = typeof(EtwCpuResidencyCaptureOwner)
                .GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
            var initialWindow = initialSession.GetType().GetProperty("Window")!.GetValue(initialSession)!;
            var previousWindowEnd = FeedWindow(reader, sampling);
            await UntilAsync(() => reader.Read() is not null);
            await Task.Delay(250);
            Assert.Equal(1, broker.SubscribeCount);
            Assert.True(IsWindowActive(reader));
            Assert.Equal(HeldKeywords, broker.Keywords);
            var previousPlanEpoch = ReadWindowPlan(sampling).Header.LastPlanEpoch;
            fast.Dispose();
            await AcknowledgeSlowWindowAsync(reader, sampling, slowInterval, previousPlanEpoch,
                initialWindow, previousWindowEnd);
            Assert.Equal(HeldKeywords, broker.Keywords);
            Assert.Equal(1, broker.ActiveCount);
            Assert.Equal(1, broker.SessionStarts);
            Assert.NotNull(reader.Read());
            slow.Dispose();
            Assert.False(sampling.Owner.Activity.HasInUseSubscriptions(6));
            Assert.Equal(HeldKeywords, broker.Keywords);
            Assert.Equal(1, broker.ActiveCount);
            await reader.StopAsync(CancellationToken.None);
            await UntilAsync(() => broker.Keywords == KernelTraceEventParser.Keywords.None);
        }
        finally { await reader.StopAsync(CancellationToken.None); }
    }

    private static EtwCpuResidencyCaptureOwner? CaptureOwner(EtwCpuCoreResidencyReader reader)
        => (EtwCpuResidencyCaptureOwner?)typeof(EtwCpuCoreResidencyReader)
            .GetField("captureOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader);

    private static async Task AcknowledgeSlowWindowAsync(EtwCpuCoreResidencyReader reader,
        HostManagerSamplingSubscriptionTestFixture sampling, TimeSpan slowInterval, ulong previousPlanEpoch,
        object lastFedWindow, long previousWindowEnd)
    {
        var capture = CaptureOwner(reader)!;
        var captureGate = typeof(EtwCpuResidencyCaptureOwner)
            .GetField("subscriptionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
        var intervalField = typeof(EtwCpuCoreResidencyReader)
            .GetField("captureInterval", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await UntilAsync(() =>
        {
            // Read the native owner outside the capture gate: production publication
            // can acquire those owners in the opposite order.
            var plan = ReadWindowPlan(sampling);
            lock (captureGate)
            {
                if (capture.IsWindowActive)
                {
                    var session = typeof(EtwCpuResidencyCaptureOwner)
                        .GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
                    var window = session.GetType().GetProperty("Window")!.GetValue(session)!;
                    if (!ReferenceEquals(window, lastFedWindow))
                    {
                        previousWindowEnd = FeedWindow(reader, sampling, previousWindowEnd);
                        lastFedWindow = window;
                    }
                }
                // An already-overdue fast query remains due after withdrawal.
                // Settle that real replacement window before observing retained current.
                return (TimeSpan)intervalField.GetValue(reader)! == slowInterval
                    && plan.Header.LastPlanEpoch > previousPlanEpoch
                    && plan.Header.ActiveSourceCount == 1
                    && plan.Item.EffectiveIntervalMilliseconds == checked((ulong)slowInterval.TotalMilliseconds)
                    && plan.Item.LastPlannedEpoch == 0
                    && plan.Item.ScheduledDueMilliseconds > plan.Header.LastCommandAtMilliseconds
                    && !capture.IsWindowActive;
            }
        });
    }

    private static (NativeSamplingSubscriptionSnapshotHeader Header, NativeSamplingSubscriptionItemState Item)
        ReadWindowPlan(HostManagerSamplingSubscriptionTestFixture sampling)
    {
        using var role = sampling.Owner.Acquire(6);
        var capacity = role.Session.Capacity;
        var header = new NativeSamplingSubscriptionSnapshotHeader
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeSamplingSubscriptionSnapshotHeader>()
        };
        var items = new NativeSamplingSubscriptionItemState[checked((int)capacity.SnapshotItemCapacity)];
        Assert.Equal(NativeSamplingSubscriptionStatus.Ok, role.Session.Snapshot(ref header,
            new NativeSamplingSubscriptionSourceView[checked((int)capacity.SnapshotSourceCapacity)], items));
        return (header, Assert.Single(items.Take(checked((int)header.ItemOutputCount))));
    }

    private static bool IsWindowActive(EtwCpuCoreResidencyReader reader)
    {
        var owner = CaptureOwner(reader);
        return owner is not null && (bool)typeof(EtwCpuResidencyCaptureOwner).GetProperty("IsWindowActive",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner)!;
    }

    private static long FeedWindow(EtwCpuCoreResidencyReader reader,
        HostManagerSamplingSubscriptionTestFixture sampling, long previousWindowEnd = 0)
    {
        // Replay into the original session buffer, as in CpuResidencyOwnerLibraryIntegrationTests.
        var owner = Assert.IsType<EtwCpuResidencyCaptureOwner>(CaptureOwner(reader));
        var session = typeof(EtwCpuResidencyCaptureOwner).GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;
        var buffer = (CpuResidencyAggregationBuffer)session.GetType().GetProperty("Buffer")!.GetValue(session)!;
        var completion = (CpuResidencyWindowCompletion)session.GetType().GetProperty("Window")!.GetValue(session)!;
        var plan = sampling.Provider.Current.HostManager.CpuCoreResidency;
        var processors = plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray();
        var start = Math.Max(Stopwatch.GetTimestamp(), previousWindowEnd + 1);
        var now = DateTimeOffset.UtcNow;
        buffer.ObserveProcessStart(start, now, 100, 11, 1000, "recorded");
        foreach (var processor in processors) buffer.ObserveThreadStart(start, now, 100, 100 + processor);
        foreach (var processor in processors)
        {
            buffer.ObserveContextSwitch(start, now, processor, 0, 0, 100, "recorded", 100 + processor);
            completion.Observe(processor, start);
        }
        var end = start + Stopwatch.Frequency * plan.ObservationWindowMilliseconds / 1000;
        foreach (var processor in processors)
        {
            buffer.ObserveContextSwitch(end, now + plan.ObservationWindow, processor, 100, 100 + processor, 0, null, 0);
            completion.Observe(processor, end);
        }
        return end;
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        using var limit = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, limit.Token);
    }

    private sealed class Broker : IKernelEtwSessionBroker
    {
        private readonly object gate = new();
        private readonly Dictionary<long, KernelTraceEventParser.Keywords> active = [];
        private long next;
        private bool stopped;
        public int SubscribeCount { get { lock (gate) return checked((int)next); } }
        public int ActiveCount { get { lock (gate) return active.Count; } }
        public int SessionStarts { get; private set; }
        public List<long> Generations { get; } = [];
        public KernelTraceEventParser.Keywords Keywords { get { lock (gate) return active.Values.Aggregate(KernelTraceEventParser.Keywords.None, (all, value) => all | value); } }
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            long id;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(stopped, this);
                if (active.Count == 0) SessionStarts++;
                id = ++next; active.Add(id, request.Keywords);
            }
            var source = new KernelEtwEventSource();
            request.Bind(source, new(7, DateTimeOffset.UtcNow));
            source.Activate();
            Generations.Add(7);
            return new Lease(this, id, source);
        }
        public KernelEtwSessionSnapshot GetSnapshot()
        {
            lock (gate)
                return new(stopped ? "Stopped" : "Running", stopped ? "recorded broker stopped" : "recorded",
                    7, DateTimeOffset.UtcNow, Keywords, active.Count, 0);
        }
        public void Stop() { lock (gate) { stopped = true; active.Clear(); } }
        private sealed class Lease(Broker owner, long id, KernelEtwEventSource source) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose() { source.Cancel(); lock (owner.gate) owner.active.Remove(id); }
        }
    }
}
