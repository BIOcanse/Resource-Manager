using static Resource_Manager_APP.Tests.MetricSnapshotOwnerTestFixture;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed partial class MetricCatalogueSubscriptionTests
{
    [Fact]
    public void HostAdapterAdvertisesOnlyTheOriginalSamplerPeriodicBinding()
    {
        using var harness = new RecordedHostMetricFieldHarness(() => Memory(128));
        Assert.True(harness.Adapter.SupportsPeriodicDelivery);

        var unbound = new HostFieldSubscriptionBackend(harness.Backend, null!, null!);
        Assert.False(unbound.SupportsPeriodicDelivery);
        Assert.Throws<NotSupportedException>(() => unbound.Acquire("unbound", [harness.Field],
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task HighestFrequencySharedDeliversLatestCurrentAtEachSubscribersOwnTrigger()
    {
        var source = new GatedRecordedMemorySource(512);
        using var harness = new RecordedHostMetricFieldHarness(source.Read);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var slow = harness.Subscriptions.SubscribeAsync("slow-field", [harness.Field],
            TimeSpan.FromSeconds(3), FieldSubscriptionOptions.Default, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        await using var fast = harness.Subscriptions.SubscribeAsync("fast-field", [harness.Field],
            TimeSpan.FromSeconds(1), FieldSubscriptionOptions.Default, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var slowPending = slow.MoveNextAsync().AsTask();
        var fastPending = fast.MoveNextAsync().AsTask();
        await harness.Sampler.StartAsync(cancellation.Token);
        try
        {
            Assert.True(await fastPending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(512d, Assert.Single(fast.Current).Value!.Number!.Value);
            Assert.False(slowPending.IsCompleted);

            source.ArmNextRead();
            var nextFastPending = fast.MoveNextAsync().AsTask();
            await source.ArmedReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
            source.SetValueAndReleaseArmedRead(777);

            var fastValue = 512d;
            for (var delivery = 0; delivery < 5 && fastValue != 777d; delivery++)
            {
                Assert.True(await nextFastPending.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token));
                fastValue = Assert.Single(fast.Current).Value!.Number!.Value;
                Assert.True(fastValue is 512d or 777d);
                if (fastValue != 777d)
                    nextFastPending = fast.MoveNextAsync().AsTask();
            }

            Assert.Equal(777d, fastValue);
            Assert.True(await slowPending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(777d, Assert.Single(slow.Current).Value!.Number!.Value);
            Assert.Equal(harness.Core.ReadCached(harness.Field), Assert.Single(slow.Current));
        }
        finally
        {
            source.ReleaseArmedRead();
            cancellation.Cancel();
            await harness.Sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AlignedFreshSubscribersShareOneOriginalMetricQuery()
    {
        using var harness = new RecordedHostMetricFieldHarness(() => Memory(1234));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var fresh = new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.FreshOnTrigger,
            AlignSameItemPhase: true);
        await using var first = harness.Subscriptions.SubscribeAsync("fresh-one", [harness.Field],
            TimeSpan.FromSeconds(1), fresh, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        await using var second = harness.Subscriptions.SubscribeAsync("fresh-two", [harness.Field],
            TimeSpan.FromSeconds(1), fresh, cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        var firstPending = first.MoveNextAsync().AsTask();
        var secondPending = second.MoveNextAsync().AsTask();
        await harness.Sampler.StartAsync(cancellation.Token);
        try
        {
            Assert.True(await firstPending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await secondPending.WaitAsync(TimeSpan.FromSeconds(5)));
            var firstReading = Assert.Single(first.Current);
            var secondReading = Assert.Single(second.Current);
            Assert.Equal(FieldObservationStatus.Current, firstReading.Status);
            Assert.Equal(1234d, firstReading.Value!.Number!.Value);
            Assert.Equal(firstReading, secondReading);
            Assert.Equal(1, harness.MemoryReads);
            Assert.Equal(harness.Core.ReadCached(harness.Field), firstReading);
        }
        finally
        {
            cancellation.Cancel();
            await harness.Sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task FrozenFreshQueryIsUnavailableAndDirectCaptureStillWorksAfterWorkerStop()
    {
        using var harness = new RecordedHostMetricFieldHarness(() => Memory(2468));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var fresh = new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.FreshOnTrigger);
        await using var stream = harness.Subscriptions.SubscribeAsync("freeze-fresh", [harness.Field],
            TimeSpan.FromSeconds(1), fresh, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var firstPending = stream.MoveNextAsync().AsTask();
        await harness.Sampler.StartAsync(cancellation.Token);
        try
        {
            Assert.True(await firstPending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(FieldObservationStatus.Current, Assert.Single(stream.Current).Status);
            Assert.Equal(1, harness.MemoryReads);

            harness.MemoryZone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            var frozen = Assert.Single(stream.Current);
            Assert.Equal(FieldObservationStatus.Unavailable, frozen.Status);
            Assert.Null(frozen.Value);
            Assert.Null(frozen.ObservedAt);
            Assert.Equal(1, harness.MemoryReads);

            var canceledMove = stream.MoveNextAsync().AsTask();
            await Task.Delay(50, CancellationToken.None);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledMove);
            await harness.Sampler.StopAsync(CancellationToken.None);

            harness.MemoryZone.ApplyMode(ResourceManagerComputeZoneMode.Normal);
            var captured = await harness.Sampler.CaptureSnapshotAsync(
                MetricSampleRequest.ForIds([harness.RequestAddress]), CancellationToken.None);
            Assert.Equal(2468UL, captured.Memory.AvailableBytes!.Value);
            Assert.Equal(2, harness.MemoryReads);
        }
        finally
        {
            cancellation.Cancel();
            await harness.Sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task FreshBatchKeepsHealthyFieldWhenAnotherDatasetFails()
    {
        using var harness = new RecordedHostMetricFieldHarness(() => Memory(1234) with { UsagePercent = double.NaN });
        var failedField = harness.Core.Catalog.Fields.Single(item =>
            item.Definition.Name == "memory_all_physical_used_percent").Field;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = harness.Subscriptions.SubscribeAsync("mixed-fresh", [harness.Field, failedField],
            TimeSpan.FromMilliseconds(100), new(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        var next = stream.MoveNextAsync().AsTask();
        await harness.Sampler.StartAsync(timeout.Token);
        try
        {
            Assert.True(await next.WaitAsync(timeout.Token));
            Assert.Equal(FieldObservationStatus.Current, stream.Current[0].Status);
            Assert.Equal(1234d, stream.Current[0].Value!.Number);
            Assert.Null(stream.Current[1].Value);
            Assert.NotEqual(FieldObservationStatus.Current, stream.Current[1].Status);
            // The sampler keeps publishing every 100 ms, so the cache may already hold a newer publication
            // of the same healthy field by the time it is read.
            var cached = harness.Core.ReadCached(harness.Field)!;
            Assert.Equal(stream.Current[0].Status, cached.Status);
            Assert.Equal(stream.Current[0].Value, cached.Value);
            Assert.True(cached.ObservedAt >= stream.Current[0].ObservedAt);
        }
        finally { await harness.Sampler.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopWithdrawsOldLeasesAndPausedFieldDemand(bool start)
    {
        using var harness = new RecordedHostMetricFieldHarness(() => Memory(456));
        using var oldLease = harness.Subscriptions.Acquire("old-lease", [harness.Field], TimeSpan.FromMilliseconds(100));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = harness.Subscriptions.SubscribeAsync("old-stream", [harness.Field],
            TimeSpan.FromMilliseconds(100), new(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        var next = stream.MoveNextAsync().AsTask();
        if (start)
        {
            await harness.Sampler.StartAsync(timeout.Token);
            Assert.True(await next.WaitAsync(timeout.Token));
        }
        await harness.Sampler.StopAsync(CancellationToken.None);
        if (!start) Assert.False(await next.WaitAsync(timeout.Token));
        var tracker = Assert.IsType<NativeItemSamplingSubscriptionTracker<MetricSampleRequest>>(
            typeof(WindowsHardwareMetricSampler).GetField("subscriptions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(harness.Sampler));
        Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
        var current = harness.Core.ReadCached(harness.Field);
        await harness.Sampler.StartAsync(timeout.Token);
        try
        {
            Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
            Assert.Equal(current, harness.Core.ReadCached(harness.Field));
            using var newLease = harness.Subscriptions.Acquire("new-lease", [harness.Field], TimeSpan.FromSeconds(1));
            oldLease.Dispose();
            Assert.Equal((1, 1), tracker.CaptureManagedSourceState());
        }
        finally { await harness.Sampler.StopAsync(CancellationToken.None); }
    }

    private static MemoryMetrics Memory(ulong available)
        => new(4096 - available, 4096, (4096 - available) * 100d / 4096, true, "recorded-memory")
        {
            AvailableBytes = available,
            ObservationStatus = SamplingObservationStatus.Current
        };

    private sealed class GatedRecordedMemorySource(ulong initialAvailable)
    {
        private readonly TaskCompletionSource<bool> armedReadStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> releaseArmedRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private long availableBytes = checked((long)initialAvailable);
        private int armNextRead;

        internal Task ArmedReadStarted => armedReadStarted.Task;

        internal void ArmNextRead() => Interlocked.Exchange(ref armNextRead, 1);

        internal MemoryMetrics Read()
        {
            if (Interlocked.Exchange(ref armNextRead, 0) == 1)
            {
                armedReadStarted.TrySetResult(true);
                releaseArmedRead.Task.GetAwaiter().GetResult();
            }

            var available = checked((ulong)Interlocked.Read(ref availableBytes));
            return Memory(available);
        }

        internal void SetValueAndReleaseArmedRead(ulong available)
        {
            Interlocked.Exchange(ref availableBytes, checked((long)available));
            releaseArmedRead.TrySetResult(true);
        }

        internal void ReleaseArmedRead() => releaseArmedRead.TrySetResult(true);
    }

    private sealed class RecordedHostMetricFieldHarness : IDisposable
    {
        private readonly Fixture fixture;
        private readonly HttpClient client = new();
        private int memoryReads;

        internal RecordedHostMetricFieldHarness(Func<MemoryMetrics> readMemory)
        {
            fixture = new Fixture(Topology([]));
            MemoryZone = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("No NUMA demand."), () =>
            {
                Interlocked.Increment(ref memoryReads);
                return readMemory();
            });
            var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id =>
                id == MemoryZone.DisplayName ? MemoryZone : new MonitoringSourceZone(id)));
            Sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), MemoryZone, null!, null!, null!, null!,
                null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, fixture.Owner, fixture.Catalog,
                registry, fixture.Slots.Owner, fixture.Slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance);
            using (var lease = fixture.Owner.Acquire())
                fixture.Catalog.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(
                    lease.Workspace.ReadCommitted(), "CPU", "RAM", lease.WorkspaceIdentity));
            var controls = new NoControls();
            Backend = new HostReadWriteBackend(Sampler, Sampler, fixture.Catalog, controls,
                new ControlWriteLayer(controls, [], new NormalAccess()));
            Core = new ReadWriteCore(Backend, new DependencyActions(client, []));
            Adapter = new HostFieldSubscriptionBackend(Backend, Sampler, Sampler);
            Subscriptions = new FieldSubscriptionCore(Core, Adapter);
            Field = Core.Catalog.Fields.Single(item => item.Definition.Name == "memory_all_physical_available_bytes").Field;
            RequestAddress = fixture.Catalog.Current!.FieldBindings.Values.Single(binding =>
                binding.Field.Name == "memory_all_physical_available_bytes").RequestAddress;
        }

        internal WindowsMemoryMonitoringZone MemoryZone { get; }
        internal WindowsHardwareMetricSampler Sampler { get; }
        internal HostReadWriteBackend Backend { get; }
        internal HostFieldSubscriptionBackend Adapter { get; }
        internal ReadWriteCore Core { get; }
        internal FieldSubscriptionCore Subscriptions { get; }
        internal FieldReference Field { get; }
        internal string RequestAddress { get; }
        internal int MemoryReads => Volatile.Read(ref memoryReads);

        public void Dispose()
        {
            Sampler.Dispose();
            client.Dispose();
            fixture.Dispose();
        }
    }
}
