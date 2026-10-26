using System.Diagnostics;
using System.Reflection;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwCpuResidencyWindowDemandTests
{
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
            Assert.Null(output.Current);
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
            Assert.Null(output.Current);
            Assert.Equal(1, reader.CanonicalRevision);
            await UntilAsync(() => !broker.Keywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch));
            await Task.Delay(150, cancellation.Token);
            Assert.Equal(1, broker.Windows);
            Assert.Equal(1, reader.CanonicalRevision);
        }
        finally { cancellation.Cancel(); await reader.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task FreezeStopsBothWindowAndMetadataDemandWithoutErasingCurrent()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        broker.WindowReady = () => FeedWindow(reader, sampling);
        using var demand = reader.AcquireSubscription("test.freeze", TimeSpan.FromSeconds(30));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => reader.Read() is not null);
            var current = reader.Read();
            reader.ApplyMode(ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Freeze);
            await UntilAsync(() => broker.Keywords == KernelTraceEventParser.Keywords.None);
            await Task.Delay(150);
            Assert.Equal(KernelTraceEventParser.Keywords.None, broker.Keywords);
            Assert.Same(current, reader.Read());
        }
        finally { await reader.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ThirtySecondDemandPublishesOneWindowPerDueAndKeepsOnlyMetadataBetweenWindows()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        broker.WindowReady = () => FeedWindow(reader, sampling);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var output = reader.SubscribeAsync("test.thirty-seconds", TimeSpan.FromSeconds(30), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var first = output.MoveNextAsync().AsTask();
        await reader.StartAsync(cancellation.Token);
        try
        {
            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            var current = Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot>(output.Current);
            await UntilAsync(() => !broker.Keywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch));
            Assert.Equal(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread, broker.Keywords);
            Assert.Equal(1, broker.Windows);
            var second = output.MoveNextAsync().AsTask();
            await Task.Delay(150, cancellation.Token);
            Assert.False(second.IsCompleted);
            Assert.Same(current, reader.Read());
            Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(35)));
            Assert.Equal(2, broker.Windows);
            Assert.Equal(1, broker.SessionStarts);
            Assert.All(broker.Generations, generation => Assert.Equal(7, generation));
        }
        finally { cancellation.Cancel(); await reader.StopAsync(CancellationToken.None); }
        Assert.Equal(KernelTraceEventParser.Keywords.None, broker.Keywords);
        Assert.NotNull(reader.Read()); // Stopping demand does not erase current.
    }

    [Fact]
    public async Task FasterDemandKeepsCaptureContinuousAndSlowerRemainingDemandPausesOnTheSameSession()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        broker.WindowReady = () => FeedWindow(reader, sampling);
        using var slow = reader.AcquireSubscription("test.slow", TimeSpan.FromSeconds(30));
        using var fast = reader.AcquireSubscription("test.fast", TimeSpan.FromMilliseconds(100));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => reader.Read() is not null);
            await Task.Delay(250);
            Assert.Equal(1, broker.Windows);
            Assert.True(broker.Keywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch));
            fast.Dispose();
            await UntilAsync(() => broker.Keywords == (KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread));
            Assert.Equal(1, broker.SessionStarts);
            Assert.NotNull(reader.Read());
            slow.Dispose();
            await UntilAsync(() => broker.Keywords == KernelTraceEventParser.Keywords.None);
        }
        finally { await reader.StopAsync(CancellationToken.None); }
    }

    private static void FeedWindow(EtwCpuCoreResidencyReader reader, HostManagerSamplingSubscriptionTestFixture sampling)
    {
        // Replay into the original session buffer, as in CpuResidencyOwnerLibraryIntegrationTests.
        var owner = (EtwCpuResidencyCaptureOwner)typeof(EtwCpuCoreResidencyReader)
            .GetField("captureOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        var session = typeof(EtwCpuResidencyCaptureOwner).GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;
        var buffer = (CpuResidencyAggregationBuffer)session.GetType().GetProperty("Buffer")!.GetValue(session)!;
        var completion = (CpuResidencyWindowCompletion)session.GetType().GetProperty("Window")!.GetValue(session)!;
        var plan = sampling.Provider.Current.HostManager.CpuCoreResidency;
        var processors = plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray();
        var start = Stopwatch.GetTimestamp();
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
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, limit.Token);
    }

    private sealed class Broker : IKernelEtwSessionBroker
    {
        private readonly object gate = new();
        private readonly Dictionary<long, KernelTraceEventParser.Keywords> active = [];
        private long next;
        private bool stopped;
        public Action? WindowReady { get; set; }
        public int Windows { get; private set; }
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
            if (request.Keywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch))
            {
                Windows++; Generations.Add(7); WindowReady?.Invoke();
            }
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
