using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Theory]
    [InlineData(SamplingDatasetIds.ProcessGpuUsage, SchedulingProcessMetricMask.GpuUsage)]
    [InlineData(SamplingDatasetIds.ProcessGpuVram, SchedulingProcessMetricMask.GpuDedicatedMemory)]
    public async Task DatasetOnlyGpuCaptureReadsAttributionAfterAdapterExpansion(
        string datasetId, SchedulingProcessMetricMask mask)
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalogProvider = new VersionedProcessAttributionCatalogProvider();
        using var sampler = new WindowsResourceBreakdownSampler(
            new StaticMetricSampler(CreateMemorySnapshot()), catalogProvider,
            new StaticResourceResidualBreakdownProvider(), null!, null!,
            subscriptions.Provider, subscriptions.Owner, new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(static () => [Process.GetCurrentProcess()]));
        var request = new ResourceBreakdownSampleRequest([], new Dictionary<string, string>(),
            ProcessSampleDetailLevel.SmartSchedulingLite, mask)
        {
            PublicationDatasetIds = [datasetId]
        };

        await sampler.CaptureSnapshotAsync(request, CancellationToken.None);

        Assert.Equal(1, catalogProvider.CaptureCount);
    }

    [Fact]
    public async Task SubscribeAsync_WaitsForBackendPublicationBeforeFirstValue()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalogProvider = new VersionedProcessAttributionCatalogProvider();
        using var sampler = new WindowsResourceBreakdownSampler(
            new StaticMetricSampler(CreateMemorySnapshot()),
            catalogProvider,
            new StaticResourceResidualBreakdownProvider(),
            null!,
            null!,
            subscriptions.Provider,
            subscriptions.Owner,
            new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(
                static () => [Process.GetCurrentProcess()]));
        var request = ResourceBreakdownSampleRequest.ForResourceTable(
            [ResourceBreakdownMetricIds.MemoryUsage],
            new Dictionary<string, string>());

        await sampler.StartAsync(CancellationToken.None);
        ResourceBreakdownSnapshot seeded;
        await using (var seed = sampler.SubscribeAsync(
                "seed-current-value",
                request,
                TimeSpan.FromSeconds(1),
                CancellationToken.None)
            .GetAsyncEnumerator())
        {
            Assert.True(await seed.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            seeded = seed.Current;
        }

        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, seeded.Sampling.Status);
        Assert.NotNull(seeded.Sampling.LastAttemptAt);
        await sampler.StopAsync(CancellationToken.None);
        Assert.Equal(seeded.CapturedAt, sampler.ReadLatest(request).CapturedAt);
        await using var enumerator = sampler.SubscribeAsync(
                "no-bootstrap-replay",
                request,
                TimeSpan.FromSeconds(1),
                CancellationToken.None)
            .GetAsyncEnumerator();

        var first = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(100);

        Assert.False(first.IsCompleted);
        await sampler.StartAsync(CancellationToken.None);
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(
            enumerator.Current.Sampling.LastAttemptAt > seeded.Sampling.LastAttemptAt,
            "The replacement subscriber received the persisted current value instead of a new backend publication.");
        await sampler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DuplicateStartIsIdempotentAndStoppedInstanceCanRestart()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = new WindowsResourceBreakdownSampler(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            subscriptions.Owner,
            null!);

        await sampler.StartAsync(CancellationToken.None);
        var first = Assert.IsAssignableFrom<Task>(
            sampler.CaptureSamplingWorkerTask());

        await sampler.StartAsync(CancellationToken.None);
        Assert.Same(first, sampler.CaptureSamplingWorkerTask());

        await sampler.StopAsync(CancellationToken.None);
        Assert.Null(sampler.CaptureSamplingWorkerTask());

        await sampler.StartAsync(CancellationToken.None);
        var restarted = Assert.IsAssignableFrom<Task>(
            sampler.CaptureSamplingWorkerTask());
        Assert.NotSame(first, restarted);

        await sampler.StopAsync(CancellationToken.None);
        Assert.Null(sampler.CaptureSamplingWorkerTask());
    }

    [Fact]
    public void DisposeIsIdempotentForAliasedSingletonRegistrations()
    {
        var sampler = new WindowsResourceBreakdownSampler(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        sampler.Dispose();
        sampler.Dispose();
    }

    [Fact]
    public void SchedulingProcessAccounting_LiveWindowsEnumerationIsClosedAndExcludesIdle()
    {
        using var sampler = new WindowsResourceBreakdownSampler(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        var first = sampler.CaptureSchedulingProcessSampleAccounting();
        var second = sampler.CaptureSchedulingProcessSampleAccounting();

        AssertAccounting(first);
        AssertAccounting(second);
    }

    private static void AssertAccounting(
        WindowsResourceBreakdownSampler.SchedulingProcessSampleAccounting accounting)
    {
        Assert.Equal(SamplingObservationStatus.Current, accounting.Status);
        Assert.True(accounting.EnumeratedCount > 0);
        Assert.True(accounting.ExcludedCount > 0);
        Assert.Equal(0u, accounting.SkippedCount);
        Assert.Equal(
            (ulong)accounting.EnumeratedCount,
            (ulong)accounting.ExcludedCount
                + accounting.SkippedCount
                + accounting.SampleCount);
        Assert.True(accounting.GovernableIdentityCount <= accounting.SampleCount);
    }

    [Fact]
    public void SchedulingProcessAccounting_UsesOneInjectedInventoryRead()
    {
        var inner = new DelegateWindowsProcessInventoryReader(
            static () => [Process.GetCurrentProcess()]);
        var guarded = new SingleCaptureWindowsProcessInventoryReader(inner);
        using var sampler = new WindowsResourceBreakdownSampler(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            guarded);

        var accounting = sampler.CaptureSchedulingProcessSampleAccounting();

        Assert.Equal(SamplingObservationStatus.Current, accounting.Status);
        Assert.Equal(1U, accounting.EnumeratedCount);
        Assert.Equal(1U, accounting.SampleCount);
        Assert.Equal(1U, accounting.GovernableIdentityCount);
        Assert.Equal(1, guarded.AttemptCount);
        Assert.Equal(1, inner.ReadCount);
    }

    [Fact]
    public void SingleCaptureInventoryGuard_RejectsSecondAttemptBeforeDelegation()
    {
        var inner = new DelegateWindowsProcessInventoryReader(
            static () => []);
        var guarded = new SingleCaptureWindowsProcessInventoryReader(inner);

        Assert.Empty(guarded.GetProcesses());
        Assert.Throws<InvalidOperationException>(() => guarded.GetProcesses());

        Assert.Equal(2, guarded.AttemptCount);
        Assert.Equal(1, inner.ReadCount);
    }

    [Fact]
    public async Task DemandSignalIsSaturatingUnderConcurrentCallers()
    {
        using var signal = new SemaphoreSlim(0, 1);
        using var start = new ManualResetEventSlim(false);
        var callers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                WindowsResourceBreakdownSampler.ReleaseDemandSignal(signal);
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(callers);

        Assert.Equal(1, signal.CurrentCount);
    }

    [Fact]
    public void DemandSignalReleaseIgnoresDisposedSignalDuringOwnerTeardown()
    {
        var signal = new SemaphoreSlim(0, 1);
        signal.Dispose();

        var exception = Record.Exception(
            () => WindowsResourceBreakdownSampler.ReleaseDemandSignal(signal));

        Assert.Null(exception);
    }

    [Fact]
    public async Task DirectCaptureDoesNotPopulateTheHostedCurrentSlot()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalogProvider = new VersionedProcessAttributionCatalogProvider();
        var metricSampler = new StaticMetricSampler(CreateMemorySnapshot());
        using var sampler = new WindowsResourceBreakdownSampler(
            metricSampler,
            catalogProvider,
            new StaticResourceResidualBreakdownProvider(),
            null!,
            null!,
            subscriptions.Provider,
            subscriptions.Owner,
            new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(
                static () => [Process.GetCurrentProcess()]));
        var request = ResourceBreakdownSampleRequest.ForResourceTable(
            [ResourceBreakdownMetricIds.MemoryUsage],
            new Dictionary<string, string>());

        var direct = await sampler.CaptureSnapshotAsync(request, CancellationToken.None);
        Assert.Equal(1, catalogProvider.CaptureCount);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, direct.Sampling.Status);

        var hostedBeforeStart = await sampler.GetSnapshotAsync(
            request,
            CancellationToken.None);
        Assert.Equal(
            ResourceBreakdownSamplingStatuses.Warming,
            hostedBeforeStart.Sampling.Status);
        Assert.Empty(hostedBeforeStart.Bars);

        await catalogProvider.InvalidateSoftwareSnapshotAsync();
        _ = await sampler.GetSnapshotAsync(request, CancellationToken.None);

        Assert.Equal(1, catalogProvider.CaptureCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_DoesNotRunForegroundCaptureOnFailure()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        var catalogProvider = new FailingProcessAttributionCatalogProvider();
        using var sampler = new WindowsResourceBreakdownSampler(
            new StaticMetricSampler(CreateMemorySnapshot()),
            catalogProvider,
            new StaticResourceResidualBreakdownProvider(),
            null!,
            null!,
            subscriptions.Provider,
            subscriptions.Owner,
            new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(
                static () => [Process.GetCurrentProcess()]));
        var request = ResourceBreakdownSampleRequest.ForResourceTable(
            [ResourceBreakdownMetricIds.MemoryUsage],
            new Dictionary<string, string>());

        var first = await sampler.GetSnapshotAsync(request, CancellationToken.None);
        var second = await sampler.GetSnapshotAsync(request, CancellationToken.None);

        Assert.Equal(ResourceBreakdownSamplingStatuses.Warming, first.Sampling.Status);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Warming, second.Sampling.Status);
        Assert.Equal(0, catalogProvider.CaptureCount);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sampler.CaptureSnapshotAsync(request, CancellationToken.None));
        Assert.Equal(1, catalogProvider.CaptureCount);

        var hostedAfterDirectFailure = await sampler.GetSnapshotAsync(
            request,
            CancellationToken.None);
        Assert.Equal(
            ResourceBreakdownSamplingStatuses.Warming,
            hostedAfterDirectFailure.Sampling.Status);
    }

    [Fact]
    public async Task GetSnapshotAsync_ReadsOnlyTheCurrentSlotWithoutRegisteringHardwareDemand()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalogProvider = new VersionedProcessAttributionCatalogProvider();
        var metricSampler = new StaticMetricSampler(CreateMemorySnapshot());
        using var sampler = new WindowsResourceBreakdownSampler(
            metricSampler,
            catalogProvider,
            new StaticResourceResidualBreakdownProvider(),
            null!,
            null!,
            subscriptions.Provider,
            subscriptions.Owner,
            new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(
                static () => [Process.GetCurrentProcess()]));
        var request = ResourceBreakdownSampleRequest.ForResourceTable(
            [ResourceBreakdownMetricIds.MemoryUsage],
            new Dictionary<string, string>());

        var snapshot = await sampler.GetSnapshotAsync(
            request,
            CancellationToken.None);

        Assert.Equal(ResourceBreakdownSamplingStatuses.Warming, snapshot.Sampling.Status);
        Assert.Equal(0, metricSampler.GetCount);
        Assert.Equal(0, metricSampler.CaptureCount);
        Assert.Equal(0, metricSampler.AcquireSubscriptionCount);
        Assert.Equal(0, metricSampler.ActiveSubscriptions);
        Assert.Equal((0U, 0U), ReadResourceSubscriptionCounts(subscriptions.Owner));
    }

    [Fact]
    public void SchedulingSubscription_OwnsItsHardwarePrerequisiteLease()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalogProvider = new VersionedProcessAttributionCatalogProvider();
        var metricSampler = new StaticMetricSampler(CreateMemorySnapshot());
        using var sampler = new WindowsResourceBreakdownSampler(
            metricSampler,
            catalogProvider,
            new StaticResourceResidualBreakdownProvider(),
            null!,
            null!,
            subscriptions.Provider,
            subscriptions.Owner,
            new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(
                static () => [Process.GetCurrentProcess()]));

        var lease = sampler.AcquireSubscription(
            "scheduler-prerequisites",
            SchedulingProcessMetricMask.MemoryUsage
                | SchedulingProcessMetricMask.GpuUsage
                | SchedulingProcessMetricMask.GpuDedicatedMemory,
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, metricSampler.ActiveSubscriptions);
        Assert.NotNull(metricSampler.LastSubscriptionRequest);
        Assert.True(metricSampler.LastSubscriptionRequest!.Includes("memory.usage"));
        Assert.True(metricSampler.LastSubscriptionRequest.IncludesAllGpuCoreMetrics);

        lease.Dispose();
        Assert.Equal(0, metricSampler.ActiveSubscriptions);
    }

    [Fact]
    public async Task StartupCancellationTokenDoesNotOwnTheBackgroundSamplingLoop()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalogProvider = new VersionedProcessAttributionCatalogProvider();
        using var sampler = new WindowsResourceBreakdownSampler(
            new StaticMetricSampler(CreateMemorySnapshot()),
            catalogProvider,
            new StaticResourceResidualBreakdownProvider(),
            null!,
            null!,
            subscriptions.Provider,
            subscriptions.Owner,
            new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(
                static () => [Process.GetCurrentProcess()]));
        using var startup = new CancellationTokenSource();

        var request = ResourceBreakdownSampleRequest.ForResourceTable(
            [ResourceBreakdownMetricIds.MemoryUsage],
            new Dictionary<string, string>());
        using var subscription = sampler.AcquireSubscription(
            "startup-cancellation",
            request,
            TimeSpan.FromSeconds(1));
        await sampler.StartAsync(startup.Token);
        startup.Cancel();

        _ = await sampler.GetSnapshotAsync(request, CancellationToken.None);
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        ResourceBreakdownSnapshot? snapshot = null;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            snapshot = await sampler.GetSnapshotAsync(
                request,
                CancellationToken.None);
            if (snapshot.Bars.Any(static bar =>
                    bar.MetricId == ResourceBreakdownMetricIds.MemoryUsage))
            {
                break;
            }
            await Task.Delay(20);
        }

        Assert.True(
            catalogProvider.CaptureCount > 0,
            "The hosted worker did not invoke the process catalog callback.");
        Assert.NotNull(snapshot);
        Assert.Contains(
            snapshot.Bars,
            static bar => bar.MetricId == ResourceBreakdownMetricIds.MemoryUsage);
        await sampler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopClosesPublicationBeforeBlockedCaptureCanCommit()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalogProvider = new VersionedProcessAttributionCatalogProvider();
        using var blockingInventory = new BlockingWindowsProcessInventoryReader();
        using var sampler = new WindowsResourceBreakdownSampler(
            new StaticMetricSampler(CreateMemorySnapshot()),
            catalogProvider,
            new StaticResourceResidualBreakdownProvider(),
            null!,
            null!,
            subscriptions.Provider,
            subscriptions.Owner,
            new PdhProcessGpuReader(),
            blockingInventory);
        var request = ResourceBreakdownSampleRequest.ForResourceTable(
            [ResourceBreakdownMetricIds.MemoryUsage],
            new Dictionary<string, string>());
        using var subscription = sampler.AcquireSubscription(
            "blocked-stop-publication",
            request,
            TimeSpan.FromSeconds(1));

        await sampler.StartAsync(CancellationToken.None);
        Assert.True(blockingInventory.Entered.Wait(TimeSpan.FromSeconds(5)));
        using var canceledWait = new CancellationTokenSource();
        var firstStop = sampler.StopAsync(canceledWait.Token);
        canceledWait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstStop);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sampler.StartAsync(CancellationToken.None));
        var stop = sampler.StopAsync(CancellationToken.None);

        try
        {
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            blockingInventory.Release.Set();
        }

        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        var published = sampler.ReadLatest(request);
        Assert.Empty(published.Bars);
        Assert.NotEqual(
            ResourceBreakdownSamplingStatuses.Ready,
            published.Sampling.Status);
    }

    private sealed class DelegateWindowsProcessInventoryReader(
        Func<Process[]> read) : IWindowsProcessInventoryReader
    {
        private int readCount;

        internal int ReadCount => Volatile.Read(ref readCount);

        public Process[] GetProcesses()
        {
            Interlocked.Increment(ref readCount);
            return read();
        }
    }

    private sealed class SingleCaptureWindowsProcessInventoryReader(
        IWindowsProcessInventoryReader inner) : IWindowsProcessInventoryReader
    {
        private int attemptCount;

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        public Process[] GetProcesses()
        {
            if (Interlocked.Increment(ref attemptCount) != 1)
            {
                throw new InvalidOperationException(
                    "The guarded Windows process inventory may be read exactly once.");
            }
            return inner.GetProcesses();
        }
    }

    private static HardwareMetricSnapshot CreateMemorySnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return new HardwareMetricSnapshot(
            now,
            new CpuMetrics(
                "test",
                0,
                true,
                CpuMetricObservationStatus.Complete,
                1,
                1,
                1,
                1,
                100,
                "test",
                new CpuSensorMetrics(
                    new HardwareSensorProviderState("test", "Unavailable", null),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(1, 2, 50, true, "test"),
            new VirtualMemoryMetrics(0, 0, 0, "test", false),
            [],
            new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                0,
                []),
            new Dictionary<string, MetricValue>
            {
                [ResourceBreakdownMetricIds.MemoryUsage] = new(
                    ResourceBreakdownMetricIds.MemoryUsage,
                    "Memory",
                    "Memory",
                    "50%",
                    50,
                    "%",
                    50,
                    null)
            })
        {
            WorkspaceIdentity = 1,
            ConfigurationGeneration = 1,
            CatalogGeneration = 1,
            CommittedGeneration = 1,
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(
                StringComparer.OrdinalIgnoreCase)
            {
                [SamplingDatasetIds.SystemMemoryUsage] = new(
                    SamplingDatasetIds.SystemMemoryUsage,
                    SamplingObservationStatus.Current,
                    1,
                    now.UtcTicks,
                    1,
                    1,
                    1,
                    1)
                {
                    LastAttemptAtUtcTicks = now.UtcTicks,
                    LastSuccessAtUtcTicks = now.UtcTicks
                }
            }
        };
    }

    private sealed class StaticMetricSampler(HardwareMetricSnapshot snapshot) :
        IMetricSampler,
        IMetricSnapshotObservationSource
    {
        private int activeSubscriptions;
        private int acquireSubscriptionCount;
        private int captureCount;
        private int getCount;

        internal int ActiveSubscriptions => Volatile.Read(ref activeSubscriptions);

        internal int AcquireSubscriptionCount => Volatile.Read(ref acquireSubscriptionCount);

        internal int CaptureCount => Volatile.Read(ref captureCount);

        internal int GetCount => Volatile.Read(ref getCount);

        internal MetricSampleRequest? LastSubscriptionRequest { get; private set; }

        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => GetSnapshotAsync(MetricSampleRequest.All, cancellationToken);

        public Task<HardwareMetricSnapshot> GetSnapshotAsync(
            MetricSampleRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref getCount);
            return Task.FromResult(snapshot);
        }

        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(
            MetricSampleRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref captureCount);
            return Task.FromResult(snapshot);
        }

        public IDisposable AcquireSubscription(
            string subscriptionId,
            MetricSampleRequest request,
            TimeSpan refreshInterval)
        {
            LastSubscriptionRequest = request;
            Interlocked.Increment(ref acquireSubscriptionCount);
            Interlocked.Increment(ref activeSubscriptions);
            return new CallbackSubscription(
                () => Interlocked.Decrement(ref activeSubscriptions));
        }

        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request)
            => snapshot;
    }

    private static (uint ActiveSourceCount, uint KnownItemCount)
        ReadResourceSubscriptionCounts(HostManagerSamplingSubscriptionOwner owner)
    {
        return owner.Execute(3, session =>
        {
            var capacity = session.Capacity;
            var header = new NativeSamplingSubscriptionSnapshotHeader
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = checked((uint)Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>())
            };
            var sources = new NativeSamplingSubscriptionSourceView[
                checked((int)capacity.SnapshotSourceCapacity)];
            var items = new NativeSamplingSubscriptionItemState[
                checked((int)capacity.SnapshotItemCapacity)];
            var status = session.Snapshot(ref header, sources, items);
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, status);
            return (header.ActiveSourceCount, header.KnownItemCount);
        });
    }

    private sealed class BlockingWindowsProcessInventoryReader :
        IWindowsProcessInventoryReader,
        IDisposable
    {
        internal ManualResetEventSlim Entered { get; } = new(false);

        internal ManualResetEventSlim Release { get; } = new(false);

        public Process[] GetProcesses()
        {
            Entered.Set();
            Release.Wait();
            return [Process.GetCurrentProcess()];
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class CallbackSubscription(Action dispose) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                dispose();
            }
        }
    }

    private sealed class VersionedProcessAttributionCatalogProvider
        : IRuntimeProcessAttributionCatalogProvider,
          IDisposable
    {
        private readonly RuntimeProcessAttributionCatalog catalog =
            RuntimeProcessAttributionCatalog.CreateOrReuse(
                null,
                [],
                [],
                [],
                new NullResolvers(),
                new NullResolvers(),
                new NullResolvers(),
                new NullResolvers(),
                SoftwareIdentityCatalogTestData.Empty,
                SoftwareIdentityCatalogTestData.Owner);
        private long generation;
        private int captureCount;

        public long SoftwareSnapshotGeneration => Volatile.Read(ref generation);

        internal int CaptureCount => Volatile.Read(ref captureCount);

        public Task<RuntimeProcessAttributionCatalog> GetCatalogAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref captureCount);
            return Task.FromResult(catalog);
        }

        public Task InvalidateSoftwareSnapshotAsync()
        {
            Interlocked.Increment(ref generation);
            return Task.CompletedTask;
        }

        public void Dispose()
            => catalog.Dispose();
    }

    private sealed class FailingProcessAttributionCatalogProvider
        : IRuntimeProcessAttributionCatalogProvider
    {
        private int captureCount;

        public long SoftwareSnapshotGeneration => 0;

        internal int CaptureCount => Volatile.Read(ref captureCount);

        public Task<RuntimeProcessAttributionCatalog> GetCatalogAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref captureCount);
            return Task.FromException<RuntimeProcessAttributionCatalog>(
                new InvalidOperationException("catalog unavailable"));
        }

        public Task InvalidateSoftwareSnapshotAsync() => Task.CompletedTask;
    }

    private sealed class NullResolvers :
        IRuntimePackageIdentityResolver,
        IRuntimeServiceIdentityResolver,
        IRuntimeRootIdentityResolver,
        IRuntimeSystemProcessClassifier
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }
}
