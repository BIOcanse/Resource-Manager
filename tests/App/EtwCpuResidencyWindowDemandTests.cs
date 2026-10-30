using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit.Abstractions;
using ResourceManager.Subscription.Native;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwCpuResidencyWindowDemandTests(ITestOutputHelper testOutput)
{
    private const KernelTraceEventParser.Keywords MetadataKeywords = KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread;
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
            await UntilAsync(() => !IsWindowActive(reader) && broker.Keywords == MetadataKeywords);
            await Task.Delay(150, cancellation.Token);
            Assert.Equal(MetadataKeywords, broker.Keywords);
            Assert.Equal(2, broker.SubscribeCount);
            Assert.Equal(1, broker.ActiveCount);
            Assert.Equal(1, reader.CanonicalRevision);
        }
        finally { cancellation.Cancel(); await reader.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CompletedWindowRetainsTheRunLeaseWithoutErasingCurrent()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        using var demand = reader.AcquireSubscription("test.completed-window", TimeSpan.FromSeconds(30));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => IsWindowActive(reader));
            FeedWindow(reader, sampling);
            await UntilAsync(() => reader.Read() is not null);
            var current = reader.Read();
            await UntilAsync(() => !IsWindowActive(reader) && broker.Keywords == MetadataKeywords);
            await Task.Delay(150);
            Assert.Equal(MetadataKeywords, broker.Keywords);
            Assert.Equal(2, broker.SubscribeCount);
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
            await UntilAsync(() => !IsWindowActive(reader) && broker.Keywords == MetadataKeywords);
            Assert.Equal(MetadataKeywords, broker.Keywords);
            Assert.Equal(2, broker.SubscribeCount);
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
            await UntilAsync(() => !IsWindowActive(reader) && broker.Keywords == MetadataKeywords);
            Assert.Equal(MetadataKeywords, broker.Keywords);
            Assert.Equal(3, broker.SubscribeCount);
            Assert.Equal(1, broker.ActiveCount);
            Assert.Equal(1, broker.SessionStarts);
            Assert.All(broker.Generations, generation => Assert.Equal(7, generation));
        }
        finally { cancellation.Cancel(); await reader.StopAsync(CancellationToken.None); }
        Assert.Equal(KernelTraceEventParser.Keywords.None, broker.Keywords);
        Assert.NotNull(reader.Read()); // Stopping demand does not erase current.
    }

    [Fact]
    public async Task ThirtySecondDemandPublishesColdAndSteadyWindowsThirtySecondsApart()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(85));
        await using var output = reader.SubscribeAsync("test.recorded-cadence", TimeSpan.FromSeconds(30), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var next = output.MoveNextAsync().AsTask();
        var publications = new List<long>();
        var measuredThrough = new List<DateTimeOffset>();
        var memoryTrace = new List<object>();
        await reader.StartAsync(cancellation.Token);
        try
        {
            for (var index = 0; index < 3; index++)
            {
                await UntilAsync(() => IsWindowActive(reader) && broker.Keywords == HeldKeywords,
                    TimeSpan.FromSeconds(35));
                await FeedElapsedWindowAsync(reader, sampling, cancellation.Token, diagnostics =>
                {
                    Assert.True(diagnostics.RetainedBucketBytes > 0);
                    Assert.True(diagnostics.ActivePairCount > 0);
                    memoryTrace.Add(new { phase = "window", index, at = DateTimeOffset.UtcNow,
                        qpc = Stopwatch.GetTimestamp(), diagnostics });
                });
                Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token));
                var snapshot = Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot>(output.Current);
                publications.Add(Stopwatch.GetTimestamp());
                measuredThrough.Add(snapshot.MeasuredThrough);
                await UntilAsync(() => !IsWindowActive(reader) && broker.Keywords == MetadataKeywords);
                var idleMemory = CaptureOwner(reader)!.GetDiagnostics().Aggregation!.Value;
                Assert.Equal(0, idleMemory.RetainedBucketBytes);
                Assert.Equal(0, idleMemory.ActivePairCount);
                memoryTrace.Add(new { phase = "idle", index, at = DateTimeOffset.UtcNow,
                    qpc = Stopwatch.GetTimestamp(), diagnostics = idleMemory });
                Assert.Equal(1, broker.ActiveCount);
                if (index < 2) next = output.MoveNextAsync().AsTask();
            }
            // Include the cold-to-steady transition: preparing at due-window
            // shortened that first real publication interval to 25 seconds.
            for (var index = 1; index < publications.Count; index++)
            {
                var actual = Stopwatch.GetElapsedTime(publications[index - 1], publications[index]);
                var coverage = measuredThrough[index] - measuredThrough[index - 1];
                Assert.True(actual >= TimeSpan.FromMilliseconds(29500), $"Publication spacing {index}: {actual}.");
                Assert.True(coverage >= TimeSpan.FromMilliseconds(29500), $"Recorded coverage spacing {index}: {coverage}.");
            }
        }
        finally
        {
            testOutput.WriteLine(JsonSerializer.Serialize(memoryTrace));
            cancellation.Cancel();
            await reader.StopAsync(CancellationToken.None);
        }
        Assert.Equal(KernelTraceEventParser.Keywords.None, broker.Keywords);
        Assert.Equal(0, broker.ActiveCount);
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
            Assert.Equal(2, broker.SubscribeCount);
            Assert.True(IsWindowActive(reader));
            Assert.Equal(HeldKeywords, broker.Keywords);
            var previousPlanEpoch = ReadWindowPlan(sampling).Header.LastPlanEpoch;
            fast.Dispose();
            await AcknowledgeSlowWindowAsync(reader, sampling, slowInterval, previousPlanEpoch,
                initialWindow, previousWindowEnd);
            await UntilAsync(() => broker.Keywords == MetadataKeywords);
            Assert.Equal(MetadataKeywords, broker.Keywords);
            Assert.Equal(1, broker.ActiveCount);
            Assert.Equal(1, broker.SessionStarts);
            Assert.NotNull(reader.Read());
            slow.Dispose();
            Assert.False(sampling.Owner.Activity.HasInUseSubscriptions(6));
            await UntilAsync(() => broker.Keywords == MetadataKeywords);
            Assert.Equal(MetadataKeywords, broker.Keywords);
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

    private static async Task FeedElapsedWindowAsync(EtwCpuCoreResidencyReader reader,
        HostManagerSamplingSubscriptionTestFixture sampling, CancellationToken cancellationToken,
        Action<CpuResidencyAggregationDiagnostics>? beforeCompletion = null)
    {
        var owner = Assert.IsType<EtwCpuResidencyCaptureOwner>(CaptureOwner(reader));
        var session = typeof(EtwCpuResidencyCaptureOwner).GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;
        var buffer = (CpuResidencyAggregationBuffer)session.GetType().GetProperty("Buffer")!.GetValue(session)!;
        var completion = (CpuResidencyWindowCompletion)session.GetType().GetProperty("Window")!.GetValue(session)!;
        var plan = sampling.Provider.Current.HostManager.CpuCoreResidency;
        var processors = plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray();
        var start = Stopwatch.GetTimestamp();
        var at = DateTimeOffset.UtcNow;
        buffer.ObserveProcessStart(start, at, 100, 11, 1000, "recorded");
        foreach (var processor in processors)
        {
            buffer.ObserveThreadStart(start, at, 100, 100 + processor);
            buffer.ObserveContextSwitch(start, at, processor, 0, 0, 100, "recorded", 100 + processor);
            completion.Observe(processor, start);
        }
        // Real elapsed time is deliberate: the native planner and this reader
        // use the production monotonic clock, with only the ETW input replayed.
        await Task.Delay(plan.ObservationWindow, cancellationToken);
        var end = start + Stopwatch.Frequency * plan.ObservationWindowMilliseconds / 1000;
        foreach (var processor in processors)
        {
            buffer.ObserveContextSwitch(end, at + plan.ObservationWindow, processor, 100, 100 + processor, 0, null, 0);
            if (processor == processors[^1]) beforeCompletion?.Invoke(buffer.GetDiagnostics());
            completion.Observe(processor, end);
        }
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
