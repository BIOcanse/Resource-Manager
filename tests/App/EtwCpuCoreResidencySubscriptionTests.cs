using System.Collections.Frozen;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.App.Domain.CpuTopology;
using Resource_Manager_APP.Tests.Oracles.KernelEtw;
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
        | KernelTraceEventParser.Keywords.Thread;

    [Fact]
    public async Task SameReaderCanRestartAndBindFreshSubscriptionAfterStop()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new CrossThreadBindingBroker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        long previousWindowEnd = 0;
        ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot firstValue;
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using (var firstRun = reader.SubscribeAsync("first-run", TimeSpan.FromMilliseconds(100), firstCancellation.Token)
            .GetAsyncEnumerator(firstCancellation.Token))
        {
            Task<bool>? pending = null;
            try
            {
                var next = pending = firstRun.MoveNextAsync().AsTask();
                await reader.StartAsync(default);
                await broker.BindingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(broker.Resource.Generation, reader.GetDiagnostics().SessionGeneration);
                await WaitUntilAsync(() => IsWindowActive(reader));
                previousWindowEnd = FeedCpuWindow(reader, sampling, 100, previousWindowEnd);
                Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
                firstValue = Assert.IsType<CpuCoreResidencySnapshot>(firstRun.Current);
                Assert.Contains(firstValue.Processes, process => process.ProcessId == 100);
                await reader.StopAsync(default);
            }
            finally
            {
                firstCancellation.Cancel();
                if (pending is not null)
                    try { await pending; }
                    catch (OperationCanceledException) when (firstCancellation.IsCancellationRequested) { }
            }
        }
        Assert.Equal(2, broker.DisposeCount);
        await reader.StartAsync(default);
        try
        {
            using var secondCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            await using var secondRun = reader.SubscribeAsync("second-run", TimeSpan.FromMilliseconds(100), secondCancellation.Token)
                .GetAsyncEnumerator(secondCancellation.Token);
            Task<bool>? pending = null;
            try
            {
                var next = pending = secondRun.MoveNextAsync().AsTask();
                await WaitUntilAsync(() => broker.SubscribeCount == 4 && reader.GetDiagnostics().SessionGeneration == broker.Resource.Generation);
                Assert.Equal(broker.Resource.Generation, reader.GetDiagnostics().SessionGeneration);
                await WaitUntilAsync(() => IsWindowActive(reader));
                previousWindowEnd = FeedCpuWindow(reader, sampling, 200, previousWindowEnd);
                Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
                var secondValue = Assert.IsType<CpuCoreResidencySnapshot>(secondRun.Current);
                Assert.NotSame(firstValue, secondValue);
                Assert.Contains(secondValue.Processes, process => process.ProcessId == 200);
                Assert.DoesNotContain(secondValue.Processes, process => process.ProcessId == 100);
                Assert.Same(secondValue, reader.Read());
            }
            finally
            {
                secondCancellation.Cancel();
                if (pending is not null)
                    try { await pending; }
                    catch (OperationCanceledException) when (secondCancellation.IsCancellationRequested) { }
            }
        }
        finally { await reader.StopAsync(default); }
        Assert.Equal(4, broker.DisposeCount);
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
        return NativeCpuReaderTestFeed.ClosedWindow(reader, sampling, processId, processId * 10L,
            processId, previousWindowEnd, publishPrelude: false);
    }

    [Fact]
    public async Task NativeLossDuringInventoryScanCancelsOldWritesAndCoalescesTheNextScan()
    {
        using var broker = new CpuResidencyHeldTestBroker();
        using var releaseFirst = new ManualResetEventSlim();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0; var active = 0; var overlap = 0;
        using var owner = new EtwCpuResidencyCaptureOwner(broker, InventoryConfiguration, new(), null, (inventory, shouldSeed) =>
        {
            if (Interlocked.Increment(ref active) != 1) Interlocked.Increment(ref overlap);
            try
            {
                if (Interlocked.Increment(ref count) == 1)
                {
                    started.TrySetResult(true);
                    if (!releaseFirst.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Native inventory was not released.");
                    Assert.False(shouldSeed()); Assert.Null(inventory.SeedProcess(999, 9999, "Stale"));
                }
                else
                {
                    Assert.True(shouldSeed()); inventory.SeedProcess(100, 2000, "Current")!.SeedThread(10);
                    recovered.TrySetResult(true);
                }
            }
            catch (Exception error) { recovered.TrySetException(error); throw; }
            finally { Interlocked.Decrement(ref active); }
        });
        owner.EnsureStarted();
        owner.BeginWindow();
        broker.Resource.Publish(broker.Resource.Tick);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var initial = owner.GetDiagnostics().Aggregation!.Value.ContinuityEpoch;
            broker.Resource.Loss(broker.Resource.Tick + 1, 1); broker.Resource.Publish(broker.Resource.Tick); owner.Collect();
            Assert.True(owner.GetDiagnostics().Aggregation!.Value.ContinuityEpoch > initial);
            var next = owner.GetDiagnostics().Aggregation!.Value.ContinuityEpoch;
            broker.Resource.Loss(broker.Resource.Tick + 1, 2); broker.Resource.Publish(broker.Resource.Tick); owner.Collect();
            Assert.True(owner.GetDiagnostics().Aggregation!.Value.ContinuityEpoch > next);
            Assert.Equal(1, count); releaseFirst.Set();
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            broker.Resource.Publish(broker.Resource.Tick);
            Assert.Equal(2, count); Assert.Equal(0, overlap); Assert.Equal(1, broker.Resource.Creations);
            Assert.Equal(1, owner.GetDiagnostics().Aggregation!.Value.CurrentProcessCount);
        }
        finally { releaseFirst.Set(); owner.Stop(); }
    }
    [Fact]
    public async Task NativeCumulativeEventLossRefreshesInventoryOnceWithoutRebindingTheSession()
    {
        using var broker = new CpuResidencyHeldTestBroker();
        var first = new TaskCompletionSource<EtwCpuResidencyCaptureOwner.InventorySeed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<EtwCpuResidencyCaptureOwner.InventorySeed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var owner = new EtwCpuResidencyCaptureOwner(broker, InventoryConfiguration, new(), null, (inventory, shouldSeed) =>
        {
            if (!shouldSeed()) return;
            var attempt = Interlocked.Increment(ref count);
            inventory.SeedProcess(100, attempt * 1000L, "Current")!.SeedThread(10);
            if (attempt == 1) first.TrySetResult(inventory);
            if (attempt == 2) second.TrySetResult(inventory);
        });
        owner.EnsureStarted();
        owner.BeginWindow();
        var old = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        broker.Resource.Publish(broker.Resource.Tick);
        var before = owner.GetDiagnostics().Aggregation!.Value.ContinuityEpoch;
        broker.Resource.Loss(broker.Resource.Tick + 1, 1); broker.Resource.Publish(broker.Resource.Tick); owner.Collect();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        broker.Resource.Publish(broker.Resource.Tick);
        Assert.Null(old.SeedProcess(999, 9999, "Stale"));
        Assert.Equal(broker.GetSnapshot().Generation, owner.GetDiagnostics().SessionGeneration);
        Assert.Equal(1, owner.GetDiagnostics().Aggregation!.Value.CurrentProcessCount);
        Assert.True(owner.GetDiagnostics().Aggregation!.Value.ContinuityEpoch > before);
        await Task.Delay(250); Assert.Equal(2, count); Assert.Equal(1, broker.Resource.Creations); Assert.Null(owner.Read());
    }

    private static CpuResidencyCaptureConfiguration InventoryConfiguration() => new(1000, 32,
        new Dictionary<int, CpuResidencyCoreAccounting> { [0] = new("core:0", "", 1, 1) }.ToFrozenDictionary(), "native inventory", 50);
    [Fact]
    public async Task StartingSubscriptionAllowsTheBrokerToBindOnAnotherThread()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new CrossThreadBindingBroker();
        using var reader = new EtwCpuCoreResidencyReader(
            broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);
        using var demand = reader.AcquireSubscription("test.cross-thread-binding", TimeSpan.FromSeconds(1));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await broker.BindingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(broker.Resource.Generation, reader.GetDiagnostics().SessionGeneration);
        }
        finally
        {
            await reader.StopAsync(CancellationToken.None);
        }
        Assert.Equal(broker.SubscribeCount, broker.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringPendingSamplingSettlesThePlanBeforeReset(bool dispose)
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new CrossThreadBindingBroker();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);
        broker.BeforeLeaseReturn = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        using var demand = reader.AcquireSubscription("test.stop-pending", TimeSpan.FromSeconds(1));
        await reader.StartAsync(CancellationToken.None);
        Task stopping = Task.CompletedTask;
        try
        {
            var entry = await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(ReferenceEquals(entry, entered.Task),
                $"Broker entry was not reached within 5 seconds: subscriptions={broker.SubscribeCount}, releases={broker.DisposeCount}, "
                + $"binding={broker.LastBinding?.Status}, bindingError={broker.LastBinding?.Exception}, "
                + $"bindingCompleted={broker.BindingCompleted.Task.Status}, "
                + $"bindingResult={(broker.BindingCompleted.Task.IsCompletedSuccessfully ? broker.BindingCompleted.Task.Result.ToString() : "pending")}, "
                + $"worker={reader.ExecuteTask?.Status}, workerError={reader.ExecuteTask?.Exception}.");
            if (dispose) stopping = Task.Run(reader.Dispose);
            else stopping = reader.StopAsync(CancellationToken.None);
            // Task.Run disposal must actually enter BeginStop before the pending bind is released.
            await WaitUntilAsync(() => (bool)typeof(EtwCpuCoreResidencyReader)
                .GetField("stopped", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!);
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
    public async Task SubscriptionBeforeRunDoesNotPublishASyntheticCurrentValue()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new UnavailableBroker();
        using var reader = CreateReader(sampling, broker);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var subscription = reader.SubscribeAsync(
                "test.before-run",
                TimeSpan.FromSeconds(1),
                cancellation.Token)
            .GetAsyncEnumerator();
        var next = subscription.MoveNextAsync().AsTask();

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

        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            Interlocked.Increment(ref subscribeCount);
            throw new InvalidOperationException("Fixture: ETW unavailable.");
        }

        public KernelEtwSessionSnapshot GetSnapshot()
            => new("Unavailable", "Fixture: ETW unavailable.", 0,
                DateTimeOffset.UnixEpoch, KernelTraceEventParser.Keywords.None, 0, 0);
    }

    private sealed class CrossThreadBindingBroker : CpuResidencyHeldTestBroker { internal CrossThreadBindingBroker() : base(37, DateTimeOffset.UnixEpoch, true) { } }

    private sealed class PassiveUnavailableBroker : IKernelEtwSessionBroker
    {
        private int subscribeCount;
        private int disposeCount;
        private int durationCount;
        public int SubscribeCount => Volatile.Read(ref subscribeCount);
        public int DisposeCount => Volatile.Read(ref disposeCount);

        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            var duration = request.Keywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch);
            if (duration) Interlocked.Increment(ref durationCount); else Interlocked.Increment(ref subscribeCount);
            return new Lease(this, duration);
        }

        public KernelEtwSessionSnapshot GetSnapshot()
            => new(
                "Unavailable",
                "Fixture: processing is unavailable after lease creation.",
                0,
                DateTimeOffset.UnixEpoch,
                KernelTraceEventParser.Keywords.None,
                SubscribeCount - DisposeCount + Volatile.Read(ref durationCount),
                0);

        private sealed class Lease(PassiveUnavailableBroker owner, bool duration) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();

            private int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    if (duration) Interlocked.Decrement(ref owner.durationCount); else Interlocked.Increment(ref owner.disposeCount);
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
