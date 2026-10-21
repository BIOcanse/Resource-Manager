using static Resource_Manager_APP.Tests.MetricSnapshotOwnerTestFixture;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;

namespace Resource_Manager_APP.Tests;

public sealed partial class MetricCatalogueSubscriptionTests
{
    [Fact]
    public async Task DirectCoreCollectionPublishesToOriginalCacheWithoutStartingTheSubscriptionWorker()
    {
        using var fixture = new Fixture(Topology([]));
        var reads = 0;
        var value = 512UL;
        var available = true;
        CancellationTokenSource? cancelDuringRead = null;
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("No NUMA demand"), () =>
        {
            reads++;
            cancelDuringRead?.Cancel();
            return new(1024 - value, 1024, 0, available, "recorded-memory")
            { AvailableBytes = value, ObservationStatus = available ? SamplingObservationStatus.Current : SamplingObservationStatus.Unavailable };
        });
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id =>
            id == memory.DisplayName ? memory : new MonitoringSourceZone(id)));
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, fixture.Owner, fixture.Catalog,
            registry, fixture.Slots.Owner, fixture.Slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance);
        using (var lease = fixture.Owner.Acquire())
            fixture.Catalog.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(lease.Workspace.ReadCommitted(), "CPU", "RAM", lease.WorkspaceIdentity));
        using var client = new HttpClient();
        var controls = new NoControls();
        var backend = new HostReadWriteBackend(sampler, sampler, fixture.Catalog, controls,
            new ControlWriteLayer(controls, [], new NormalAccess()));
        var core = new ReadWriteCore(backend, new DependencyActions(client, []));
        var fieldSubscriptions = new FieldSubscriptionCore(core, new HostFieldSubscriptionBackend(backend, sampler, sampler));
        var field = core.Catalog.Fields.Single(item => item.Definition.Name == "memory_all_physical_available_bytes").Field;
        Assert.Null(core.ReadCached(field).Value);
        Assert.Equal(0, reads);
        Assert.Null(sampler.CaptureSamplingWorkerTask());
        Assert.Equal(512, Assert.Single(await core.CollectAsync([field])).Value!.Number);
        Assert.Equal(512, core.ReadCached(field).Value!.Number);
        Assert.Equal(1, reads);
        // Catalog probes are explicit metadata reads, not observation publications.
        await sampler.CaptureSnapshotAsync(MetricSampleRequest.CatalogProbe, CancellationToken.None);
        Assert.Equal(512, core.ReadCached(field).Value!.Number);
        Assert.Equal(1, reads);
        value = 0;
        using (var observerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            // The existing typed notification stream can observe explicit direct
            // captures without a worker; canonical field streams use periodic triggers.
            var address = backend.ResolveBindings([field])[0].Binding.RequestAddress;
            await using var observer = sampler.SubscribeAsync("direct-result-observer", MetricSampleRequest.ForIds([address]), TimeSpan.FromSeconds(1),
                observerCancellation.Token).GetAsyncEnumerator(observerCancellation.Token);
            var next = observer.MoveNextAsync().AsTask();
            Assert.False(next.IsCompleted);
            Assert.Equal(0, Assert.Single(await core.CollectAsync([field])).Value!.Number);
            Assert.True(await next);
            Assert.Equal(0, observer.Current.Items[address].NumericValue);
            Assert.Equal(0, core.ReadCached(field).Value!.Number);
        }
        Assert.Equal(0, core.ReadCached(field).Value!.Number);
        Assert.Null(sampler.CaptureSamplingWorkerTask());
        using var cancellation = new CancellationTokenSource();
        cancelDuringRead = cancellation;
        value = 3;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await core.CollectAsync([field], cancellation.Token));
        Assert.Equal(0, core.ReadCached(field).Value!.Number);
        cancelDuringRead = null;
        available = false;
        Assert.Null(Assert.Single(await core.CollectAsync([field])).Value);
        Assert.Equal(FieldObservationStatus.Unavailable, core.ReadCached(field).Status);
        Assert.Null(core.ReadCached(field).Value);
        Assert.Equal(4, reads);
    }

    [Fact]
    public void PublicFieldLeasesUseTheOriginalNativeDemandTableAndReleaseIndependently()
    {
        using var fixture = new Fixture(Topology([]));
        using var sampler = Sampler(fixture.Slots, fixture.Catalog, fixture.Owner);
        using (var lease = fixture.Owner.Acquire())
            fixture.Catalog.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(
                lease.Workspace.ReadCommitted(), "CPU", "RAM", lease.WorkspaceIdentity));
        using var client = new HttpClient();
        var controls = new NoControls();
        var backend = new HostReadWriteBackend(sampler, sampler, fixture.Catalog, controls,
            new ControlWriteLayer(controls, [], new NormalAccess()));
        var core = new ReadWriteCore(backend, new DependencyActions(client, []));
        var subscriptions = new FieldSubscriptionCore(core, new HostFieldSubscriptionBackend(backend, sampler, sampler));
        var field = core.Catalog.Fields.Single(item => item.Definition.Name == "memory_all_physical_available_bytes").Field;
        Assert.Throws<ArgumentException>(() => subscriptions.Acquire("invalid", [field with { DeviceIdentity = "other" }], TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => subscriptions.Acquire("empty", [], TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => subscriptions.Acquire("bad-interval", [field], TimeSpan.Zero));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => subscriptions.SubscribeAsync("cancelled", [field], TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.Null(sampler.CaptureSamplingWorkerTask());
        var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)typeof(WindowsHardwareMetricSampler)
            .GetField("subscriptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sampler)!;
        Assert.False(tracker.CreatePlan(DateTimeOffset.UtcNow).IsActive);
        // Equal caller labels still produce independent original sampler leases.
        using var slow = subscriptions.Acquire("same-label", [field], TimeSpan.FromSeconds(5));
        using var fast = subscriptions.Acquire("same-label", [field], TimeSpan.FromSeconds(1));
        var now = DateTimeOffset.UtcNow;
        var first = tracker.CreatePlan(now);
        Assert.Equal(2, first.Schedule.ActiveSourceCount);
        Assert.Equal(1, first.Schedule.ActiveItemCount);
        Assert.Single(first.DueItemIds);
        tracker.MarkSampled(first.Request!, now);
        // Keep this lease test away from the live-clock deadline.
        var fastNow = DateTimeOffset.UtcNow.AddSeconds(2);
        var faster = tracker.CreatePlan(fastNow);
        Assert.Single(faster.DueItemIds);
        tracker.MarkSampled(faster.Request!, fastNow);
        fast.Dispose();
        var slowOnly = tracker.CreatePlan(fastNow.AddSeconds(1));
        Assert.Null(slowOnly.Request);
        Assert.Equal(1, slowOnly.Schedule.ActiveSourceCount);
        // A slow delivery trigger may be due without a fresh sampling request.
        // The original worker settles such a plan before requesting the next one.
        if (!slowOnly.Triggers.IsEmpty)
            tracker.CompletePlan(slowOnly, fastNow.AddSeconds(1),
                ResourceManager.Subscription.Native.NativeSamplingSubscriptionCompletionStatus.Skipped);
        slow.Dispose();
        slow.Dispose();
        Assert.False(tracker.CreatePlan(fastNow.AddSeconds(2)).IsActive);
    }

    [Fact]
    public async Task PublicFieldStreamUsesOriginalWorkerAndNullFailurePublication()
    {
        using var fixture = new Fixture(Topology([]));
        var reads = 0;
        var available = true;
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("No NUMA demand"), () =>
        {
            Interlocked.Increment(ref reads);
            var current = Volatile.Read(ref available);
            return new(1024, 1024, 0, current, "recorded-memory")
            { AvailableBytes = 0, ObservationStatus = current ? SamplingObservationStatus.Current : SamplingObservationStatus.Unavailable };
        });
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id =>
            id == memory.DisplayName ? memory : new MonitoringSourceZone(id)));
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, fixture.Owner, fixture.Catalog,
            registry, fixture.Slots.Owner, fixture.Slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance);
        using (var lease = fixture.Owner.Acquire())
            fixture.Catalog.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(
                lease.Workspace.ReadCommitted(), "CPU", "RAM", lease.WorkspaceIdentity));
        using var client = new HttpClient();
        var controls = new NoControls();
        var backend = new HostReadWriteBackend(sampler, sampler, fixture.Catalog, controls,
            new ControlWriteLayer(controls, [], new NormalAccess()));
        var core = new ReadWriteCore(backend, new DependencyActions(client, []));
        var subscriptions = new FieldSubscriptionCore(core, new HostFieldSubscriptionBackend(backend, sampler, sampler));
        var field = core.Catalog.Fields.Single(item => item.Definition.Name == "memory_all_physical_available_bytes").Field;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sampler.StartAsync(cancellation.Token);
        try
        {
            await using var stream = subscriptions.SubscribeAsync("worker", [field], TimeSpan.FromSeconds(1), cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal(0, Assert.Single(stream.Current).Value!.Number);
            AssertOriginalObservation(core.ReadCached(field), Assert.Single(stream.Current));
            Assert.True(Volatile.Read(ref reads) > 0);
            Volatile.Write(ref available, false);
            do { Assert.True(await stream.MoveNextAsync()); }
            while (Assert.Single(stream.Current).Status == FieldObservationStatus.Current);
            Assert.Equal(FieldObservationStatus.Unavailable, Assert.Single(stream.Current).Status);
            Assert.Null(Assert.Single(stream.Current).Value);
            AssertOriginalObservation(core.ReadCached(field), Assert.Single(stream.Current));
            var waiting = stream.MoveNextAsync().AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
            Assert.Null(core.ReadCached(field).Value);
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
    }

    private static void AssertOriginalObservation(FieldReading current, FieldReading delivered)
    {
        Assert.Equal(current.Field, delivered.Field);
        Assert.Equal(current.SourceId, delivered.SourceId);
        if (current.ObservedAt == delivered.ObservedAt) Assert.Equal(current, delivered);
        else Assert.True(current.ObservedAt > delivered.ObservedAt,
            "The original owner may have completed a later observation after delivery, but may not precede it.");
    }

    private sealed class NoControls : IControlObjectCatalog
    {
        public ControlObjectCatalog ReadObjects() => throw new InvalidOperationException("Read APIs must not probe controls.");
    }
    private sealed class NormalAccess : IControlAccessLevel
    {
        public string Current => ControlAccessLevels.Normal;
        public Task SetAsync(string level, CancellationToken token) => throw new NotSupportedException();
    }
}
