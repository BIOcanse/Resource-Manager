using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Frames;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace Resource_Manager_APP.Tests;

public sealed class HostPresentFrameFieldQueryIntegrationTests
{
    private const int ProcessId = 81;
    private const long ProcessBirthFileTimeUtc = 134056632000000000;
    private const ulong SwapChain = 0xA0;
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(750);

    [Fact]
    public async Task HostFactoryUsesOneExactQueryOwnerForDirectAndRoleTwoConsumption()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var query = new PresentFrameFieldQuery(ProcessId, ProcessBirthFileTimeUtc,
            FramePresentSource.Dxgi, SwapChain, Window, FrameTimePercentile.P95);
        var source = new RecordedPresentSource(query);
        var services = new ServiceCollection();
        services.AddSingleton<IFrameTimingObservationSource>(source);
        services.AddSingleton<HostPresentFrameFieldQueries>();
        using var provider = services.BuildServiceProvider();
        var queries = provider.GetRequiredService<HostPresentFrameFieldQueries>();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var devices = new DeviceTopologySnapshotProvider(new RecordedTopology(), new RecordedJournal(), new(),
            sampling.Owner, NullLogger<DeviceTopologySnapshotProvider>.Instance, new RecordedAcpi());
        await using var cpu = new CpuTopologyProviderTestFixture();
        var processApi = new ProcessBasicFieldCollectorTests.Api();
        using var process = ProcessBasicFieldCollectorTests.Fixture(processApi);
        var metrics = new HostReadWriteBackend(null!, null!, new HardwareMetricCatalogState(), null!, null!);
        var host = new HostFieldBackends(metrics, new HostFieldSubscriptionBackend(metrics, null!, null!),
            devices, cpu.Reader, new HostProcessFieldBackend(process.Sampler), presentFrameQueries: queries);
        Assert.Same(queries, host.PresentFrameQueries);
        Assert.DoesNotContain(host.Reads.Catalog.Fields,
            static item => item.Field.FieldName.StartsWith("frame_", StringComparison.Ordinal));
        using var owner = host.PresentFrameQueries.Create(query);
        Assert.DoesNotContain(host.Reads.Catalog.Fields,
            static item => item.Field.FieldName.StartsWith("frame_", StringComparison.Ordinal));

        Assert.Same(query, owner.Query);
        var backend = owner.CreateBackend();
        Assert.Equal(13, backend.Catalog.Fields.Length);
        Assert.True(query.Fields.ToHashSet().SetEquals(backend.Catalog.Fields.Select(static item => item.Field)),
            "the factory owner exposes exactly this query's dynamic field identities");
        foreach (var name in new[]
        {
            FrameFieldRegistry.DisplayIntervalMs, FrameFieldRegistry.DisplayedFps,
            FrameFieldRegistry.DroppedCount, FrameFieldRegistry.DisplayIntervalP95Ms,
            FrameFieldRegistry.DisplayIntervalP99Ms
        })
        {
            var capability = Assert.Single(backend.Catalog.Fields.Where(item => item.Field.FieldName == name));
            Assert.True(capability.Read.Supported);
            Assert.Null(capability.Read.Reason);
            Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(capability.Field).Status);
        }

        var windowField = query.Field(FrameFieldRegistry.WindowDurationConfigured);
        Assert.Equal(Window.TotalSeconds, backend.ReadCached(windowField).Value?.Number);
        Assert.Equal(0, source.Acquisitions);
        Assert.Equal(0, source.ReadCount);

        var fps = query.Field(FrameFieldRegistry.FpsDerived);
        var presentTime = query.Field(FrameFieldRegistry.PresentTimeActual);
        var percentile = query.Field(FrameFieldRegistry.TimePercentileDerived);
        var direct = await backend.CollectAsync([fps, presentTime, percentile], timeout.Token);
        Assert.Equal(Window, source.LastWindow);
        Assert.Equal(1, source.Acquisitions);
        Assert.Equal(1, source.ActiveLeases);
        Assert.Equal(50d, direct.Single(static item => item.Field.FieldName == FrameFieldRegistry.FpsDerived).Value?.Number);
        Assert.Equal(0d, direct.Single(static item => item.Field.FieldName == FrameFieldRegistry.PresentTimeActual).Value?.Number);
        Assert.Equal(FrameIntervalStatistics.Compute([10d, 20d, 30d])!.P95FrameTimeMs,
            direct.Single(static item => item.Field.FieldName == FrameFieldRegistry.TimePercentileDerived).Value?.Number);
        Assert.Equal(backend.ReadCached(fps), direct.Single(static item => item.Field.FieldName == FrameFieldRegistry.FpsDerived));
        Assert.Equal(backend.ReadCached(presentTime), direct.Single(static item => item.Field.FieldName == FrameFieldRegistry.PresentTimeActual));

        var failures = new ConcurrentQueue<SubscriptionWorkerFailure>();
        var borrow = owner.BorrowSubscriptionCapture();
        Assert.Same(backend, borrow.Backend);
        Assert.Same(owner.Publications, borrow.Publications);
        var configuration = Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(borrow,
            new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000), in configuration,
            failures.Enqueue);
        await composition.StartAsync(timeout.Token);
        using var fastDemand = composition.Acquire("host-present-query-role-2-fast", [fps, percentile], TimeSpan.FromMilliseconds(100));
        using var subscriptionCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var stream = composition.SubscribeAsync("host-present-query-role-2-observer", [fps, presentTime, percentile],
            TimeSpan.FromMilliseconds(500), subscriptionCancellation.Token)
            .GetAsyncEnumerator(subscriptionCancellation.Token);
        try
        {
            await WaitForReadCountAsync(source, 3, TimeSpan.FromSeconds(5), timeout.Token);
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
            Assert.Equal(2, source.ActiveLeases);
            Assert.Equal(Window, source.LastWindow);
            Assert.Equal(50d, stream.Current.Single(static item => item.Field.FieldName == FrameFieldRegistry.FpsDerived).Value?.Number);
            Assert.Equal(FrameIntervalStatistics.Compute([10d, 20d, 30d])!.P95FrameTimeMs,
                stream.Current.Single(static item => item.Field.FieldName == FrameFieldRegistry.TimePercentileDerived).Value?.Number);
            Assert.All(stream.Current, reading => FieldReadingAssert.CurrentOrNewer(reading, backend.ReadCached(reading.Field)));

            subscriptionCancellation.Cancel();
            try
            {
                Assert.False(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
            }
            catch (OperationCanceledException) when (subscriptionCancellation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }

        fastDemand.Dispose();
        await composition.StopAsync();
        Assert.Equal(1, source.ActiveLeases);
        Assert.True(failures.IsEmpty);
        owner.Dispose();
        Assert.Equal(0, source.ActiveLeases);
    }

    private static NativeSamplingSubscriptionConfiguration Configuration() => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = checked((uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
        Generation = 1,
        MaximumSourceCount = 8,
        MaximumItemCount = 8,
        MaximumMembershipCount = 16,
        MaximumDueItemCount = 8,
        MaximumSourceViewCount = 8,
        MaximumExpiredSourceCount = 8,
        DefaultIntervalMilliseconds = 1000,
        MinimumIntervalMilliseconds = 1,
        ActiveTtlMilliseconds = 3000,
        MaximumFutureSkewMilliseconds = 1000
    };

    private static async Task WaitForReadCountAsync(RecordedPresentSource source, int minimum,
        TimeSpan waitLimit, CancellationToken cancellationToken)
    {
        using var boundedWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        boundedWait.CancelAfter(waitLimit);
        while (source.ReadCount < minimum)
            await Task.Delay(TimeSpan.FromMilliseconds(10), boundedWait.Token);
    }

    private sealed class RecordedPresentSource(PresentFrameFieldQuery query) : IFrameTimingObservationSource
    {
        private int acquisitions;
        private int activeLeases;
        private int reads;
        private TimeSpan lastWindow;

        internal int Acquisitions => Volatile.Read(ref acquisitions);
        internal int ActiveLeases => Volatile.Read(ref activeLeases);
        internal int ReadCount => Volatile.Read(ref reads);
        internal TimeSpan LastWindow => lastWindow;

        public TimeSpan Retention => ResourceManager.App.Infrastructure.Monitoring.MonitoringSettings.ProductDefaults.FramePipeline.Retention;
        public IFrameSubscriptionLease AcquireSubscription(FrameSubscriptionRequest request) => new FrameLeaseAdapter(AcquireSubscription());

        public IDisposable AcquireSubscription()
        {
            Interlocked.Increment(ref acquisitions);
            Interlocked.Increment(ref activeLeases);
            return new SourceLease(() => Interlocked.Decrement(ref activeLeases));
        }

        public FrameTimingSnapshot? Read(TimeSpan window)
        {
            lastWindow = window;
            var read = Interlocked.Increment(ref reads);
            var observedAt = DateTimeOffset.UnixEpoch.AddMilliseconds(read - 1);
            var selectedStatistics = FrameIntervalStatistics.Compute([10d, 20d, 30d])!;
            var decoyStatistics = FrameIntervalStatistics.Compute([100d, 100d])!;
            var selected = new FrameTimingStream(query.Source, query.SwapChain, observedAt, selectedStatistics);
            FrameTimingProcess[] processes =
            [
                new(query.ProcessId, query.ProcessStartFileTimeUtc,
                [
                    selected,
                    new(query.Source, query.SwapChain + 1, observedAt, decoyStatistics),
                    new(FramePresentSource.Direct3D9, query.SwapChain, observedAt, decoyStatistics)
                ]),
                new(query.ProcessId, query.ProcessStartFileTimeUtc + 1,
                    [new(query.Source, query.SwapChain, observedAt, decoyStatistics)])
            ];
            FrameTimingPresent[] presents =
            [
                new(query.ProcessId, query.ProcessStartFileTimeUtc, query.Source, query.SwapChain, observedAt),
                new(query.ProcessId, query.ProcessStartFileTimeUtc, query.Source, query.SwapChain + 1, observedAt),
                new(query.ProcessId, query.ProcessStartFileTimeUtc, FramePresentSource.Direct3D9, query.SwapChain, observedAt),
                new(query.ProcessId + 1, query.ProcessStartFileTimeUtc, query.Source, query.SwapChain, observedAt)
            ];
            return new(observedAt, window, true, processes) { LastPresents = presents };
        }

        public FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through)
            => new(through, true, []);

        private sealed class SourceLease(Action release) : IDisposable
        {
            private Action? holder = release;
            public void Dispose() => Interlocked.Exchange(ref holder, null)?.Invoke();
        }
    }

    private sealed class RecordedTopology : IDeviceTopologyReader
    {
        public DeviceTopologySnapshot ReadSnapshot() => throw new InvalidOperationException("Unexpected topology read.");
    }

    private sealed class RecordedAcpi : IAcpiFirmwareTableReader
    {
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables()
            => throw new InvalidOperationException("Unexpected ACPI read.");
    }

    private sealed class RecordedJournal : IDeviceTopologySnapshotStore
    {
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);
        public Task SaveAsync(DeviceTopologyPersistedSnapshot value, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unexpected topology persistence.");
    }
}
