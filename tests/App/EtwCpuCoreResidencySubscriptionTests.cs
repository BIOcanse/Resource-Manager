using System.Diagnostics;
using System.Reflection;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwCpuCoreResidencySubscriptionTests
{
    private const KernelTraceEventParser.Keywords HeldKeywords = KernelTraceEventParser.Keywords.Process
        | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ContextSwitch;

    [Fact]
    public async Task SameReaderCanRestartAndBindFreshSubscriptionAfterStop()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new CrossThreadBindingBroker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        long previousWindowEnd = 0;
        // Finish this fixture's initial boundaries during each run-entry bind,
        // before the fast worker can legitimately publish a warming value.
        broker.BeforeLeaseReturn = () => previousWindowEnd = FeedCpuWindow(
            reader, sampling, broker.SubscribeCount * 100, previousWindowEnd);
        ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot firstValue;
        await using (var firstRun = reader.SubscribeAsync("first-run", TimeSpan.FromMilliseconds(100), timeout.Token)
            .GetAsyncEnumerator(timeout.Token))
        {
            var next = firstRun.MoveNextAsync().AsTask();
            await reader.StartAsync(default);
            await broker.BindingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(37, reader.GetDiagnostics().SessionGeneration);
            await WaitUntilAsync(() => IsWindowActive(reader));
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
            firstValue = Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot>(firstRun.Current);
            Assert.Contains(firstValue.Processes, process => process.ProcessId == 100);
            await reader.StopAsync(default);
        }
        Assert.Equal(1, broker.DisposeCount);
        await reader.StartAsync(default);
        try
        {
            await using var secondRun = reader.SubscribeAsync("second-run", TimeSpan.FromMilliseconds(100), timeout.Token)
                .GetAsyncEnumerator(timeout.Token);
            var next = secondRun.MoveNextAsync().AsTask();
            await WaitUntilAsync(() => broker.SubscribeCount == 2 && reader.GetDiagnostics().SessionGeneration == 37);
            Assert.Equal(37, reader.GetDiagnostics().SessionGeneration);
            await WaitUntilAsync(() => IsWindowActive(reader));
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
            var secondValue = Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot>(secondRun.Current);
            Assert.NotSame(firstValue, secondValue);
            Assert.Contains(secondValue.Processes, process => process.ProcessId == 200);
            Assert.DoesNotContain(secondValue.Processes, process => process.ProcessId == 100);
            Assert.Same(secondValue, reader.Read());
        }
        finally { await reader.StopAsync(default); }
        Assert.Equal(2, broker.DisposeCount);
    }

    private static bool IsWindowActive(EtwCpuCoreResidencyReader reader)
    {
        var owner = typeof(EtwCpuCoreResidencyReader)
            .GetField("captureOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader);
        return owner is not null && (bool)typeof(EtwCpuResidencyCaptureOwner).GetProperty("IsWindowActive",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner)!;
    }

    private static long FeedCpuWindow(EtwCpuCoreResidencyReader reader,
        HostManagerSamplingSubscriptionTestFixture sampling, int processId, long previousWindowEnd)
    {
        // Use the actual reader's session buffer, as in EtwCpuResidencyWindowDemandTests.
        var owner = (EtwCpuResidencyCaptureOwner)typeof(EtwCpuCoreResidencyReader)
            .GetField("captureOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        var session = typeof(EtwCpuResidencyCaptureOwner)
            .GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        var buffer = (CpuResidencyAggregationBuffer)session.GetType().GetProperty("Buffer")!.GetValue(session)!;
        var completion = (CpuResidencyWindowCompletion)session.GetType().GetProperty("Window")!.GetValue(session)!;
        var plan = sampling.Provider.Current.HostManager.CpuCoreResidency;
        var processors = plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray();
        var start = Math.Max(Stopwatch.GetTimestamp(), previousWindowEnd + 1);
        var now = DateTimeOffset.UtcNow;
        buffer.ObserveProcessStart(start, now, processId, checked((ulong)processId), processId * 10L, "recorded");
        foreach (var processor in processors)
        {
            var threadId = processId + processor;
            buffer.ObserveThreadStart(start, now, processId, threadId);
            buffer.ObserveContextSwitch(start, now, processor, 0, 0, processId, "recorded", threadId);
            completion.Observe(processor, start);
        }
        var end = start + Stopwatch.Frequency * plan.ObservationWindowMilliseconds / 1000;
        foreach (var processor in processors)
        {
            buffer.ObserveContextSwitch(end, now + plan.ObservationWindow, processor,
                processId, processId + processor, 0, null, 0);
            completion.Observe(processor, end);
        }
        return end;
    }

    [Fact]
    public async Task LossDuringInventoryScanCancelsOldWritesAndCoalescesTheNextScan()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new CrossThreadBindingBroker();
        using var releaseFirst = new ManualResetEventSlim();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var active = 0;
        var overlap = 0;
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, (inventory, shouldSeed) =>
            {
                if (Interlocked.Increment(ref active) != 1) Interlocked.Increment(ref overlap);
                try
                {
                    var attempt = Interlocked.Increment(ref count);
                    if (attempt == 1)
                    {
                        started.TrySetResult(true);
                        if (!releaseFirst.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture inventory was not released.");
                        Assert.False(shouldSeed());
                        Assert.Null(inventory.SeedProcess(999, 9999, "Stale"));
                    }
                    else
                    {
                        Assert.True(shouldSeed());
                        inventory.SeedProcess(100, 2000, "Current")!.SeedThread(10);
                        recovered.TrySetResult(true);
                    }
                }
                catch (Exception error) { recovered.TrySetException(error); throw; }
                finally { Interlocked.Decrement(ref active); }
            });
        using var demand = reader.AcquireSubscription("test.loss-during-inventory", TimeSpan.FromMilliseconds(100));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var initial = reader.GetDiagnostics().Aggregation!.Value.ContinuityEpoch;
            Volatile.Write(ref broker.EventsLost, 1);
            await WaitUntilAsync(() => reader.GetDiagnostics().Aggregation!.Value.ContinuityEpoch > initial);
            var firstReset = reader.GetDiagnostics().Aggregation!.Value.ContinuityEpoch;
            Volatile.Write(ref broker.EventsLost, 2);
            await WaitUntilAsync(() => reader.GetDiagnostics().Aggregation!.Value.ContinuityEpoch > firstReset);
            Assert.Equal(1, Volatile.Read(ref count));
            releaseFirst.Set();
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref count));
            Assert.Equal(0, Volatile.Read(ref overlap));
            Assert.Equal(1, broker.SubscribeCount);
            Assert.Equal(1, reader.GetDiagnostics().Aggregation!.Value.CurrentProcessCount);
        }
        finally
        {
            releaseFirst.Set();
            await reader.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CumulativeEventLossRefreshesInventoryOnceWithoutRebindingTheSession()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new CrossThreadBindingBroker();
        var firstInventory = new TaskCompletionSource<CpuResidencyAggregationBuffer.InventorySeed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInventory = new TaskCompletionSource<CpuResidencyAggregationBuffer.InventorySeed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seedCount = 0;
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, (inventory, shouldSeed) =>
            {
                if (!shouldSeed()) return;
                var count = Interlocked.Increment(ref seedCount);
                inventory.SeedProcess(100, count * 1000L, "Current")!.SeedThread(10);
                if (count == 1) firstInventory.TrySetResult(inventory);
                if (count == 2) secondInventory.TrySetResult(inventory);
            });
        using var demand = reader.AcquireSubscription("test.loss-inventory", TimeSpan.FromMilliseconds(100));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            var old = await firstInventory.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => reader.GetDiagnostics().Aggregation?.CurrentThreadCount == 1);
            var before = reader.GetDiagnostics().Aggregation!.Value.ContinuityEpoch;
            Volatile.Write(ref broker.EventsLost, 1);
            await secondInventory.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(old.SeedProcess(999, 9999, "Stale"));
            Assert.Equal(37, reader.GetDiagnostics().SessionGeneration);
            Assert.Equal(1, reader.GetDiagnostics().Aggregation!.Value.CurrentProcessCount);
            Assert.True(reader.GetDiagnostics().Aggregation!.Value.ContinuityEpoch > before);
            await Task.Delay(250);
            Assert.Equal(2, Volatile.Read(ref seedCount));
            Assert.Equal(1, broker.SubscribeCount);
            Assert.Null(reader.Read()); // No context switches were supplied by this fixture.
        }
        finally { await reader.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task FreezeWhileRunLeaseIsBeingAcquiredRetainsTheReturnedLeaseUntilStop()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new CrossThreadBindingBroker();
        using var reader = new EtwCpuCoreResidencyReader(
            broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        broker.BeforeLeaseReturn = () => reader.ApplyMode(ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Freeze);
        using var demand = reader.AcquireSubscription("test.freeze-during-acquisition", TimeSpan.FromSeconds(1));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await broker.BindingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitUntilAsync(() => reader.GetDiagnostics().SessionGeneration == 37);
            Assert.False(IsWindowActive(reader));
            Assert.Equal(0, broker.DisposeCount);
            Assert.Equal(1, broker.SubscribeCount);
            Assert.Equal(HeldKeywords, broker.Keywords);
            Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
            Assert.Null(reader.Read());
        }
        finally
        {
            await reader.StopAsync(CancellationToken.None);
        }
        Assert.Equal(1, broker.DisposeCount);
        Assert.Equal(KernelTraceEventParser.Keywords.None, broker.GetSnapshot().EnabledKeywords);
    }

    [Fact]
    public async Task StartingSubscriptionAllowsTheBrokerToBindOnAnotherThread()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new CrossThreadBindingBroker();
        using var reader = new EtwCpuCoreResidencyReader(
            broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        using var demand = reader.AcquireSubscription("test.cross-thread-binding", TimeSpan.FromSeconds(1));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await broker.BindingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(37, reader.GetDiagnostics().SessionGeneration);
        }
        finally
        {
            await reader.StopAsync(CancellationToken.None);
        }
        Assert.Equal(1, broker.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringPendingSamplingSettlesThePlanBeforeReset(bool dispose)
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new CrossThreadBindingBroker();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        broker.BeforeLeaseReturn = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        using var demand = reader.AcquireSubscription("test.stop-pending", TimeSpan.FromSeconds(1));
        await reader.StartAsync(CancellationToken.None);
        Task stopping = Task.CompletedTask;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            if (dispose) stopping = Task.Run(reader.Dispose);
            else stopping = reader.StopAsync(CancellationToken.None);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, broker.DisposeCount);
        }
        finally
        {
            release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await reader.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, broker.SubscribeCount);
        Assert.Equal(1, broker.DisposeCount);
        Assert.Null(reader.GetDiagnostics().SessionGeneration);
        Assert.Null(reader.Read());
        if (dispose)
            Assert.Throws<InvalidOperationException>(() => reader.AcquireSubscription(
                "test.after-dispose", TimeSpan.FromSeconds(1)));
        else
        {
            using var retained = reader.AcquireSubscription("test.after-stop", TimeSpan.FromSeconds(1));
            Assert.Equal(1, broker.SubscribeCount);
        }
    }

    [Fact]
    public void ReadAndSubscriptionRegistrationDoNotStartEtw()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new UnavailableBroker();
        using var reader = CreateReader(sampling, broker);

        var first = reader.Read();
        using var subscription = reader.AcquireSubscription(
            "test.registration", TimeSpan.FromSeconds(1));

        Assert.Same(first, reader.Read());
        Assert.Equal(0, broker.SubscribeCount);
    }

    [Fact]
    public async Task SubscriptionPublishesEmptyResultOnlyAfterBackgroundAttempt()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new UnavailableBroker();
        using var reader = CreateReader(sampling, broker);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscription = reader.SubscribeAsync(
            "test.callback", TimeSpan.FromSeconds(1), cancellation.Token)
            .GetAsyncEnumerator();
        var next = subscription.MoveNextAsync().AsTask();
        Assert.False(next.IsCompleted);
        Assert.Equal(0, broker.SubscribeCount);

        await reader.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await next);
            Assert.True(broker.SubscribeCount > 0);
            Assert.Equal("cpu-residency-session-unavailable",
                Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencyUnavailable>(subscription.Current).ReasonCode);
            Assert.Same(reader.ReadObservation(), subscription.Current);
            await cancellation.CancelAsync();
            Assert.False(await subscription.MoveNextAsync());
        }
        finally
        {
            await reader.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RemovingTheLastSubscriptionPublishesInactiveAndExplicitStopReleasesTheUnavailableKernelLease()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new PassiveUnavailableBroker();
        using var reader = new EtwCpuCoreResidencyReader(
            broker,
            sampling.Provider,
            sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);
        var subscription = reader.AcquireSubscription(
            "test.release-unavailable",
            TimeSpan.FromSeconds(1));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => broker.SubscribeCount == 1);
            subscription.Dispose();
            Assert.False(sampling.Owner.Activity.HasInUseSubscriptions(6));
            Assert.Equal(0, broker.DisposeCount);
            await reader.StopAsync(CancellationToken.None);
            await WaitUntilAsync(() => broker.DisposeCount == 1);

            Assert.Null(reader.Read());
        }
        finally
        {
            subscription.Dispose();
            await reader.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopBeforeStartRetainsSubscriptionsAndExplicitStartResumesThem()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new PassiveUnavailableBroker();
        using var reader = new EtwCpuCoreResidencyReader(
            broker,
            sampling.Provider,
            sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);
        var pending = reader.AcquireSubscription(
            "test.stop-before-start",
            TimeSpan.FromSeconds(1));

        await reader.StopAsync(CancellationToken.None);
        using var whileStopped = reader.AcquireSubscription("test.while-stopped", TimeSpan.FromSeconds(1));
        Assert.Equal(0, broker.SubscribeCount);
        Assert.True(sampling.Owner.Activity.HasInUseSubscriptions(6));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => broker.SubscribeCount == 1);
            Assert.Equal(0, broker.DisposeCount);
            Assert.Null(reader.Read());
            pending.Dispose();
            using var fresh = reader.AcquireSubscription("test.after-restart", TimeSpan.FromSeconds(1));
            Assert.Equal(1, broker.SubscribeCount);
        }
        finally
        {
            pending.Dispose();
            await reader.StopAsync(CancellationToken.None);
        }
        Assert.Equal(1, broker.DisposeCount);
    }

    [Fact]
    public async Task FreezeDoesNotPublishASyntheticCurrentValue()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new UnavailableBroker();
        using var reader = CreateReader(sampling, broker);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var subscription = reader.SubscribeAsync(
                "test.freeze",
                TimeSpan.FromSeconds(1),
                cancellation.Token)
            .GetAsyncEnumerator();
        var next = subscription.MoveNextAsync().AsTask();

        reader.ApplyMode(ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Freeze);
        await Task.Delay(50);

        Assert.False(next.IsCompleted);
        Assert.Null(reader.Read());
        Assert.Equal(0, broker.SubscribeCount);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    private static EtwCpuCoreResidencyReader CreateReader(
        HostManagerSamplingSubscriptionTestFixture sampling,
        UnavailableBroker broker)
        => new(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);

    private sealed class UnavailableBroker : IKernelEtwSessionBroker
    {
        private int subscribeCount;
        public int SubscribeCount => Volatile.Read(ref subscribeCount);

        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            Interlocked.Increment(ref subscribeCount);
            throw new InvalidOperationException("Fixture: ETW unavailable.");
        }

        public KernelEtwSessionSnapshot GetSnapshot()
            => new("Unavailable", "Fixture: ETW unavailable.", 0,
                DateTimeOffset.UnixEpoch, KernelTraceEventParser.Keywords.None, 0, 0);
    }

    private sealed class CrossThreadBindingBroker : IKernelEtwSessionBroker
    {
        private int disposed;
        private int subscribeCount;
        public int EventsLost;
        public int SubscribeCount => Volatile.Read(ref subscribeCount);
        public int DisposeCount => Volatile.Read(ref disposed);
        public TaskCompletionSource<bool> BindingCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? BeforeLeaseReturn { get; set; }
        private KernelTraceEventParser.Keywords requestedKeywords;
        public KernelTraceEventParser.Keywords Keywords => SubscribeCount > DisposeCount
            ? requestedKeywords : KernelTraceEventParser.Keywords.None;

        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            Interlocked.Increment(ref subscribeCount);
            requestedKeywords = request.Keywords;
            var source = new KernelEtwEventSource();
            var binding = Task.Run(() => request.Bind(source, new(37, DateTimeOffset.UnixEpoch)));
            var completed = binding.Wait(TimeSpan.FromSeconds(1));
            if (!completed)
            {
                BindingCompleted.TrySetResult(false);
                throw new TimeoutException("The reader held its lock across broker subscription.");
            }
            source.Activate();
            BeforeLeaseReturn?.Invoke();
            BindingCompleted.TrySetResult(true);
            return new Lease(this, source);
        }

        public KernelEtwSessionSnapshot GetSnapshot()
            => new("Running", "fixture", 37, DateTimeOffset.UnixEpoch,
                Keywords, SubscribeCount - DisposeCount, Volatile.Read(ref EventsLost));

        private sealed class Lease(CrossThreadBindingBroker owner, KernelEtwEventSource source) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();

            private int released;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                source.Cancel();
                Interlocked.Increment(ref owner.disposed);
            }
        }
    }

    private sealed class PassiveUnavailableBroker : IKernelEtwSessionBroker
    {
        private int subscribeCount;
        private int disposeCount;
        public int SubscribeCount => Volatile.Read(ref subscribeCount);
        public int DisposeCount => Volatile.Read(ref disposeCount);

        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            Interlocked.Increment(ref subscribeCount);
            return new Lease(this);
        }

        public KernelEtwSessionSnapshot GetSnapshot()
            => new(
                "Unavailable",
                "Fixture: processing is unavailable after lease creation.",
                0,
                DateTimeOffset.UnixEpoch,
                KernelTraceEventParser.Keywords.None,
                SubscribeCount - DisposeCount,
                0);

        private sealed class Lease(PassiveUnavailableBroker owner) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();

            private int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    Interlocked.Increment(ref owner.disposeCount);
                }
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
