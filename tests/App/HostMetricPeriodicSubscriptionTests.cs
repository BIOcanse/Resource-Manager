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
    public async Task HardwareFieldStreamSurvivesAProducerFaultAndControllerRestart()
    {
        using var harness = new RecordedHostMetricFieldHarness(() => Memory(345));
        var backend = new ProducerSubscriptionCurrentBackend(harness.Backend);
        var originalSource = harness.Backend.ReadCached(harness.Field).SourceId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var snapshots = harness.Sampler.SubscribeAsync("snapshot-held-through-fault",
            MetricSampleRequest.ForIds([harness.RequestAddress]), TimeSpan.FromMilliseconds(100), timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        var pendingSnapshot = snapshots.MoveNextAsync().AsTask();
        await using var stream = harness.Sampler.SubscribeFieldAsync("held-through-fault", [harness.Field],
            harness.Backend.ResolveBindings([harness.Field]), TimeSpan.FromMilliseconds(100),
            FieldSubscriptionOptions.Default, backend, timeout.Token).GetAsyncEnumerator(timeout.Token);
        var pending = stream.MoveNextAsync().AsTask();
        await using var controller = await ProducerSubscriptionRestartHost<WindowsHardwareMetricSampler>
            .StartAsync(harness.Sampler, static producer => producer.CaptureSamplingWorkerTask(), backend);
        var firstRun = await controller.FirstRun;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstRun.WaitAsync(timeout.Token));
        Assert.Equal(ProducerSubscriptionCurrentBackend.FailureMessage, failure.Message);
        await controller.AssertControllerRestartedAsync();
        Assert.True(await pending.WaitAsync(timeout.Token));
        Assert.True(await pendingSnapshot.WaitAsync(timeout.Token));
        Assert.NotNull(snapshots.Current);
        var recovered = Assert.Single(stream.Current);
        while (recovered.Status != FieldObservationStatus.Current)
        {
            Assert.Contains(recovered.Status, new[] { FieldObservationStatus.Warming, FieldObservationStatus.Unavailable });
            Assert.Equal(harness.Field, recovered.Field);
            Assert.Equal(originalSource, recovered.SourceId);
            Assert.Null(recovered.Value);
            Assert.Null(recovered.ObservedAt);
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
            recovered = Assert.Single(stream.Current);
        }
        Assert.Equal(FieldObservationStatus.Current, recovered.Status);
        Assert.Equal(harness.Field, recovered.Field);
        Assert.Equal(originalSource, recovered.SourceId);
        Assert.Equal(345d, recovered.Value?.Number);
        FieldReadingAssert.CurrentOrNewer(recovered, harness.Backend.ReadCached(harness.Field));
        Assert.True(harness.MemoryReads >= 1); // Shared delivery may reuse original current before the next source query is due.
    }

    [Fact]
    public async Task HardwareFieldDemandAcceptedWhileStoppingNotifiesActivityAndSurvivesNextEntry()
    {
        var source = new GatedRecordedMemorySource(456);
        using var harness = new RecordedHostMetricFieldHarness(source.Read);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var activity = new List<bool>();
        harness.SamplingOwner.Activity.RoleChanged += role =>
        {
            if (role == 1) activity.Add(harness.SamplingOwner.Activity.HasInUseSubscriptions(1));
        };
        var first = harness.Subscriptions.Acquire("first-run", [harness.Field], TimeSpan.FromMilliseconds(100));
        source.ArmNextRead();
        await harness.Sampler.StartAsync(CancellationToken.None);
        await source.ArmedReadStarted.WaitAsync(timeout.Token);
        var stop = harness.Sampler.StopAsync(CancellationToken.None);
        try
        {
            Assert.False(stop.IsCompleted);
            first.Dispose();
            Assert.False(harness.SamplingOwner.Activity.HasInUseSubscriptions(1));
            await using var late = harness.Subscriptions.SubscribeAsync("accepted-while-stopping", [harness.Field],
                TimeSpan.FromMilliseconds(100), new(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
                .GetAsyncEnumerator(timeout.Token);
            var pending = late.MoveNextAsync().AsTask();
            Assert.False(pending.IsCompleted);
            Assert.True(harness.SamplingOwner.Activity.HasInUseSubscriptions(1));
            Assert.Equal(new[] { true, false, true }, activity);
            source.SetValueAndReleaseArmedRead(789);
            await stop.WaitAsync(timeout.Token);
            Assert.False(pending.IsCompleted);
            await harness.Sampler.StartAsync(CancellationToken.None);
            Assert.True(await pending.WaitAsync(timeout.Token));
            Assert.Equal(789d, Assert.Single(late.Current).Value?.Number);
            await late.DisposeAsync();
            Assert.False(harness.SamplingOwner.Activity.HasInUseSubscriptions(1));
            Assert.Equal(new[] { true, false, true, false }, activity);
        }
        finally { first.Dispose(); source.ReleaseArmedRead(); await harness.Sampler.StopAsync(CancellationToken.None); }
    }

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
            FieldReadingAssert.CurrentOrNewer(Assert.Single(slow.Current), harness.Core.ReadCached(harness.Field));
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
        // Only the first query returns 1234. Later periodic queries may run before the assertions under load;
        // a second query for the same trigger would hand one subscriber 2345.
        var queries = 0;
        using var harness = new RecordedHostMetricFieldHarness(() => Memory(Interlocked.Increment(ref queries) == 1 ? 1234UL : 2345UL));
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
            Assert.True(harness.MemoryReads >= 1);
            FieldReadingAssert.CurrentOrNewer(firstReading, harness.Core.ReadCached(harness.Field));
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
            Assert.Equal(2468d, captured.Items[harness.RequestAddress].NumericValue);
            Assert.Equal(2468d, harness.Core.ReadCached(harness.Field).Value!.Number);
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
    public async Task StopRetainsHeldLeasesAndPausedFieldSubscriptionsUntilTheyAreReleased(bool start)
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
        if (start) next = stream.MoveNextAsync().AsTask();
        Assert.False(next.IsCompleted);
        var tracker = Assert.IsType<NativeItemSamplingSubscriptionTracker<MetricSampleRequest>>(
            typeof(WindowsHardwareMetricSampler).GetField("subscriptions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(harness.Sampler));
        Assert.Equal((2, 2), tracker.CaptureManagedSourceState());
        var current = harness.Core.ReadCached(harness.Field);
        using (var stoppedLease = harness.Subscriptions.Acquire("while-stopped", [harness.Field], TimeSpan.FromSeconds(1)))
            Assert.Equal((3, 3), tracker.CaptureManagedSourceState());
        Assert.Equal((2, 2), tracker.CaptureManagedSourceState());
        Assert.Equal(current, harness.Core.ReadCached(harness.Field));
        await harness.Sampler.StartAsync(timeout.Token);
        try
        {
            Assert.True(await next.WaitAsync(timeout.Token));
            Assert.Equal(456d, Assert.Single(stream.Current).Value?.Number);
            Assert.Equal((2, 2), tracker.CaptureManagedSourceState());
            await stream.DisposeAsync();
            Assert.Equal((1, 1), tracker.CaptureManagedSourceState());
            using var newLease = harness.Subscriptions.Acquire("new-lease", [harness.Field], TimeSpan.FromSeconds(1));
            oldLease.Dispose();
            Assert.Equal((1, 1), tracker.CaptureManagedSourceState());
        }
        finally { await harness.Sampler.StopAsync(CancellationToken.None); }
        Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
    }

    private static MemoryMetrics Memory(ulong available)
        => new(UsedBytes: 4096 - available, TotalBytes: 4096, UsagePercent: (4096 - available) * 100d / 4096, IsUsageAvailable: true)
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

    internal sealed class RecordedHostMetricFieldHarness : IDisposable
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
                fixture.Catalog.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
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
        internal HardwareMetricCatalogState Catalog => fixture.Catalog;
        internal HostManagerSamplingSubscriptionOwner SamplingOwner => fixture.Slots.Owner;
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
