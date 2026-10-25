using static Resource_Manager_APP.Tests.MetricSnapshotOwnerTestFixture;
using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class MetricCatalogueSubscriptionTests
{
    [Fact]
    public void ChangedGpuInventoryGenerationRequiresNewWorkspaceEvenWhenNamesAndHandlesMatch()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var gpu = new NativeMetricSnapshotGpuCatalogIdentity(0, "GPU", "gpu-exact", 1, 1, 1, true);
        var first = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(plan, [gpu], [], [], NativeMetricSnapshotCatalogHandleMap.Empty);
        var same = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(plan, [gpu], [], [], first.HandleMap);
        Assert.True(HostManagerMetricSnapshotOwner.CanRetainCatalog(first, same));
        var next = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(plan, [gpu with { InventoryGeneration = 2 }], [], [], first.HandleMap);
        Assert.False(HostManagerMetricSnapshotOwner.CanRetainCatalog(first, next));
    }

    [Fact]
    public async Task CatalogueOnlyPublicationCarriesGpuIdentityWithoutUsageOrSensorValues()
    {
        using var fixture = new Fixture(Topology([]) with
        {
            GpuAdapters = [new(0, "GPU", "gpu-exact", 1, 1, 1, true)]
        });
        using var sampler = Sampler(fixture.Slots, fixture.Catalog, fixture.Owner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using var stream = sampler.SubscribeAsync("gpu-directory", MetricSampleRequest.CatalogProbe,
                TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await stream.MoveNextAsync());
            AssertCatalogOnlyGpu(stream.Current);
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CatalogueOnlyCapturePublishesGpuIdentityWithoutMetricValues()
    {
        using var fixture = new Fixture(Topology([]) with
        {
            GpuAdapters = [new(0, "GPU", "gpu-exact", 1, 1, 1, true)]
        });
        using var sampler = Sampler(fixture.Slots, fixture.Catalog, fixture.Owner);

        var captured = await sampler.CaptureSnapshotAsync(
            MetricSampleRequest.CatalogProbe, CancellationToken.None);
        var published = sampler.ReadLatest(MetricSampleRequest.CatalogProbe)
            ?? throw new InvalidOperationException("catalogue capture was not published");

        // The direct full projection can carry unavailable N/A descriptors
        // for unrelated configured fields. The current-store composition is
        // limited to the catalog and GPU-inventory datasets.
        AssertCatalogOnlyGpu(captured, allowUnavailableDescriptorItems: true);
        AssertCatalogOnlyGpu(published);
    }

    [Fact]
    public void ConfigurationApplyRetainsStorageAndFanDeclarationsWhenOptionalEnumerationFails()
    {
        var topology = Topology([new(7, "disk-first", 1)]) with
        {
            FanStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete,
            SystemFans = [new(0, "fan-first", 1)]
        };
        using var fixture = new Fixture(topology);
        var original = fixture.Owner.CurrentWorkspaceIdentity;
        fixture.Topology = Topology([], storageUnavailable: true);
        var desired = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["hot_publish"]!["metric_snapshot"]!["source_policies"]![0]!["priority"] = 11;
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
        });
        fixture.Slots.Provider.Publish(fixture.Slots.Provider.Current with
        {
            Version = fixture.Slots.Provider.Current.Version + 1,
            HostManager = desired
        });
        Assert.NotEqual(original, fixture.Owner.CurrentWorkspaceIdentity);
        using var lease = fixture.Owner.Acquire();
        var catalog = lease.Workspace.ReadCatalog();
        Assert.Equal("disk-first", Assert.Single(catalog.StorageIdentities).ExactIdentityKey);
        Assert.Equal("fan-first", Assert.Single(catalog.SystemFanIdentities).ExactIdentityKey);
    }

    [Fact]
    public void CatalogueUsesOneSharedSlotWithHighestFrequencyAndStopsWhenLeasesAreReleased()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = Sampler(slots, new(), null!);
        using var slow = sampler.AcquireSubscription("slow-catalogue", MetricSampleRequest.CatalogProbe, TimeSpan.FromSeconds(5));
        using var fast = sampler.AcquireSubscription("fast-catalogue", MetricSampleRequest.CatalogProbe, TimeSpan.FromSeconds(1));
        var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)typeof(WindowsHardwareMetricSampler)
            .GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
        // Use a future command after the real acquisition timestamps without
        // selecting an epoch boundary; the item owns its completion phase.
        var now = DateTimeOffset.UtcNow.AddSeconds(5);
        var first = tracker.CreatePlan(now);
        Assert.True(first.Request!.IsCatalogProbe);
        Assert.Equal([SamplingDatasetIds.SystemMetricCatalog], first.DueItemIds);
        Assert.Equal(1, first.Schedule.ActiveItemCount);
        Assert.Equal(2, first.Schedule.ActiveSourceCount);
        var completion = tracker.MarkSampled(first.Request, now);
        Assert.Equal(TimeSpan.FromSeconds(1), completion.Delay);
        var fastAt = completion.Schedule.NextWakeAt!.Value.AddMilliseconds(100);
        var fastDue = tracker.CreatePlan(fastAt);
        Assert.True(fastDue.Request!.IsCatalogProbe);
        tracker.MarkSampled(fastDue.Request, fastAt);
        fast.Dispose();
        var slowOnly = tracker.CreatePlan(now.AddSeconds(2));
        Assert.Null(slowOnly.Request);
        Assert.Equal(1, slowOnly.Schedule.ActiveSourceCount);
        slow.Dispose();
        Assert.False(tracker.CreatePlan(now.AddSeconds(3)).IsActive);
    }

    [Fact]
    public async Task CatalogueReadsDoNotAcquireSampleGateOrNativeWorkspace()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var catalog = new HardwareMetricCatalogState();
        using var sampler = Sampler(slots, catalog, null!);
        var gate = (SemaphoreSlim)typeof(WindowsHardwareMetricSampler).GetField("sampleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
        await gate.WaitAsync();
        try
        {
            var read = sampler.GetSnapshotAsync(MetricSampleRequest.CatalogProbe, CancellationToken.None);
            Assert.True(read.IsCompletedSuccessfully);
            Assert.Equal(0UL, (await read).CatalogGeneration);
            Assert.Null(sampler.ReadLatest(MetricSampleRequest.CatalogProbe));
            Assert.Equal([SamplingDatasetIds.SystemMetricCatalog], sampler.GetPublicationItemIds(MetricSampleRequest.CatalogProbe));
        }
        finally { gate.Release(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CatalogueSubscriptionDiscoversFirstDiskWithoutSamplingAnyNumbers(bool initiallyUnavailable)
    {
        using var fixture = new Fixture(Topology([], initiallyUnavailable));
        using var sampler = Sampler(fixture.Slots, fixture.Catalog, fixture.Owner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using var stream = sampler.SubscribeAsync("empty-directory", MetricSampleRequest.CatalogProbe,
                TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await stream.MoveNextAsync());
            var first = stream.Current;
            Assert.DoesNotContain(first.FieldBindings.Values, binding => binding.Field.Resource == MetricResource.Storage);
            Assert.Equal(initiallyUnavailable ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current,
                first.Datasets[SamplingDatasetIds.StorageInventory].Status);
            Assert.Equal(SamplingObservationStatus.Current, first.Datasets[SamplingDatasetIds.SystemMetricCatalog].Status);
            Assert.Empty(first.Items);
            var firstWorkspace = fixture.Owner.CurrentWorkspaceIdentity;
            fixture.Topology = Topology([new(7, "disk-first", 2) { SupportsNvmeHealth = true }]);
            fixture.Owner.RequestTopologyRefresh();
            HardwareMetricSnapshot second;
            do
            {
                Assert.True(await stream.MoveNextAsync());
                second = stream.Current;
            } while (!second.FieldBindings.Values.Any(binding => binding.DeviceIdentity == "disk-first"));
            Assert.NotEqual(firstWorkspace, fixture.Owner.CurrentWorkspaceIdentity);
            Assert.Equal(SamplingObservationStatus.Current, second.Datasets[SamplingDatasetIds.StorageInventory].Status);
            Assert.Contains(second.FieldBindings.Values, binding => binding.Field.Code == MetricFieldCode.NvmePercentageUsed);
            Assert.Empty(second.Items);
            Assert.All(second.Datasets.Where(pair => pair.Key != SamplingDatasetIds.SystemMetricCatalog
                && pair.Key != SamplingDatasetIds.StorageInventory && pair.Key != SamplingDatasetIds.FanInventory),
                pair => Assert.NotEqual(SamplingObservationStatus.Current, pair.Value.Status));
            var gate = (SemaphoreSlim)typeof(WindowsHardwareMetricSampler).GetField("sampleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            await gate.WaitAsync(timeout.Token);
            try
            {
                var reads = fixture.TopologyReads;
                var cached = sampler.GetSnapshotAsync(MetricSampleRequest.CatalogProbe, timeout.Token);
                Assert.True(cached.IsCompletedSuccessfully);
                Assert.Equal(second.CatalogGeneration, (await cached).CatalogGeneration);
                Assert.Equal(reads, fixture.TopologyReads);
            }
            finally { gate.Release(); }
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task OwnerKeepsUnchangedWorkspaceAndRetainsUnavailableOptionalDeclarations()
    {
        using var fixture = new Fixture(Topology([new(7, "disk-first", 1) { SupportsNvmeHealth = true }]));
        var original = fixture.Owner.CurrentWorkspaceIdentity;
        fixture.Owner.RequestTopologyRefresh();
        fixture.Owner.RefreshTopologyIfRequested();
        Assert.Equal(original, fixture.Owner.CurrentWorkspaceIdentity);
        fixture.Topology = Topology([], storageUnavailable: true);
        fixture.Owner.RequestTopologyRefresh();
        var failed = fixture.Owner.RefreshTopologyIfRequested()!;
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, failed.StorageStatus);
        Assert.Equal(original, fixture.Owner.CurrentWorkspaceIdentity);
        using (var lease = fixture.Owner.Acquire()) Assert.Equal("disk-first", Assert.Single(lease.Workspace.ReadCatalog().StorageIdentities).ExactIdentityKey);
        fixture.Topology = Topology([]);
        fixture.Owner.RequestTopologyRefresh();
        fixture.Owner.RefreshTopologyIfRequested();
        Assert.NotEqual(original, fixture.Owner.CurrentWorkspaceIdentity);
        using var removed = fixture.Owner.Acquire();
        Assert.Empty(removed.Workspace.ReadCatalog().StorageIdentities);
        await Task.CompletedTask;
    }

    [Fact]
    public void ChangedCatalogueInvalidatesOldValuesAndDropsRemovedRequestAddresses()
    {
        var old = StorageMetricSnapshotTestData.CommittedSnapshot();
        using var cache = new LastSuccessfulHardwareMetricSnapshot();
        cache.Publish(old, MetricSampleRequest.All);
        var replaced = old with
        {
            WorkspaceIdentity = old.WorkspaceIdentity + 1,
            CapturedAt = old.CapturedAt.AddSeconds(1),
            Items = new Dictionary<string, MetricReadingValue>(),
            FieldBindings = old.FieldBindings.ToDictionary(static pair => pair.Key,
                static pair => pair.Value with { DeviceIdentity = "replacement-disk" }),
            Datasets = old.Datasets.Where(pair => pair.Key != "disk.7.readErrorsTotal")
                .ToDictionary(static pair => pair.Key, static pair => pair.Value with { Status = SamplingObservationStatus.Warming })
        };
        replaced = WindowsHardwareMetricSampler.AddCatalogObservations(replaced, Topology([]), replaced.CapturedAt);
        cache.Publish(replaced, MetricSampleRequest.CatalogProbe);
        var current = cache.Read()!;
        Assert.Empty(current.Items);
        Assert.DoesNotContain("disk.7.readErrorsTotal", current.Datasets.Keys);
        Assert.All(Assert.Single(MetricDeviceFields.Read(current, "replacement-disk")).Fields, field =>
        {
            Assert.NotEqual(SamplingObservationStatus.Current, field.Status);
            Assert.Null(field.NumericValue);
            Assert.Null(field.IntegerValue);
        });
    }

    [Fact]
    public void OptionalEnumerationFailureIsDifferentFromSuccessfulEmptyDirectory()
    {
        var empty = WindowsStorageSensorsMonitoringZone.ReadIdentities(1, (_, _) => new([], null));
        var failed = WindowsStorageSensorsMonitoringZone.ReadIdentities(1, (_, _) => new([], "query failed"));
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, empty.Status);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, failed.Status);
        var emptyFans = HardwareMonitorWmiMonitoringZone.ReadFanIdentities(1, (_, _) => new([], null));
        var failedFans = HardwareMonitorWmiMonitoringZone.ReadFanIdentities(1, (_, _) => new([], "missing provider"));
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, emptyFans.Status);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, failedFans.Status);
    }

    [Fact]
    public void CatalogueRefreshKeepsLegacyRequestsSubscribableBeforeFirstSample()
    {
        var snapshot = StorageMetricSnapshotTestData.CommittedSnapshot();
        var state = new HardwareMetricCatalogState();
        state.PublishCatalogProbe(snapshot with { Items = new Dictionary<string, MetricReadingValue>() });
        var selected = state.SelectSubscribable(MetricSampleRequest.ForIds(["disk.7.wearPercent"]));
        Assert.Equal(["disk.7.wearPercent"], selected.Ids);
        var field = snapshot.FieldBindings.Values.Single(binding => binding.Field.Name == "storage_all_wear_reported");
        Assert.Contains("disk.7.wearPercent", field.RequestAliases);
    }

    private static WindowsHardwareMetricSampler Sampler(HostManagerSamplingSubscriptionTestFixture slots,
        HardwareMetricCatalogState catalog, HostManagerMetricSnapshotOwner owner)
        => new(new WindowsCpuMonitoringZone(), new WindowsMemoryMonitoringZone(), null!, null!, null!, null!,
            null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, catalog,
            new MonitoringSourceZoneRegistry([]), slots.Owner, slots.Provider,
            NullLogger<WindowsHardwareMetricSampler>.Instance);

    private static void AssertCatalogOnlyGpu(
        HardwareMetricSnapshot snapshot,
        bool allowUnavailableDescriptorItems = false)
    {
        var gpu = Assert.Single(snapshot.Gpus);
        Assert.Equal("gpu-exact", gpu.IdentityKey);
        Assert.False(gpu.IsUsageAvailable);
        Assert.Equal(0, gpu.UsagePercent);
        Assert.All(new double?[]
        {
            gpu.Sensors.PowerWatts,
            gpu.Sensors.PowerLimitWatts,
            gpu.Sensors.BoardPowerWatts,
            gpu.Sensors.TemperatureCelsius,
            gpu.Sensors.HotspotTemperatureCelsius,
            gpu.Sensors.IntakeTemperatureCelsius,
            gpu.Sensors.FanSpeedPercent,
            gpu.Sensors.FanSpeedRpm,
            gpu.Sensors.CoreVoltageVolts,
            gpu.Sensors.CurrentAmps
        }, static value => Assert.Null(value));
        if (allowUnavailableDescriptorItems)
        {
            Assert.All(snapshot.Items.Values, static item =>
            {
                Assert.Null(item.NumericValue);
                Assert.Null(item.Percent);
                Assert.Null(item.Total);
                Assert.Null(item.IntegerValue);
                Assert.Null(item.BytesValue);
                Assert.Null(item.BooleanValue);
                Assert.Null(item.SampleDurationMilliseconds);
                Assert.NotEmpty(item.Id);
            });
        }
        else
        {
            Assert.Empty(snapshot.Items);
        }
        Assert.DoesNotContain(MetricDeviceFields.Read(snapshot).SelectMany(static device => device.Fields),
            static field => field.Name == "gpu_all_engine_busy_percent"
                && field.Status == SamplingObservationStatus.Current);
    }

}
