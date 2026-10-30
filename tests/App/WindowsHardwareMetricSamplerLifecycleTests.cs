using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsHardwareMetricSamplerLifecycleTests
{
    [Theory]
    [InlineData(1352)]
    [InlineData(1557)]
    public void AllMetricsFromRecordedCatalogReachesRealNativeTracker(int metricCount)
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        var catalog = AllMetricSubscriptionTestData.RecordedCatalog(metricCount);
        using var sampler = CreateSampler(subscriptions, catalog);
        var expanded = catalog.SelectSubscribable(MetricSampleRequest.All);
        Assert.False(expanded.IsAll);
        Assert.True(expanded.Ids.Count > 1024);
        using var lease = sampler.AcquireSubscription("targeted-recording:recorded", MetricSampleRequest.All, TimeSpan.FromSeconds(1));
        var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)typeof(WindowsHardwareMetricSampler)
            .GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
        var plan = tracker.CreatePlan(DateTimeOffset.UtcNow);
        Assert.NotNull(plan.Request);
        Assert.Equal(sampler.GetPublicationItemIds(expanded).Order(), plan.DueItemIds.Order());
        tracker.MarkSampled(plan.Request!, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task AllMetricsPushAndSingleFetchAdmitAndReleaseTheRealCatalog()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = CreateSampler(subscriptions, AllMetricSubscriptionTestData.RecordedCatalog());
        var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)typeof(WindowsHardwareMetricSampler)
            .GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
        for (var mode = 0; mode < 3; mode++)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var stream = sampler.SubscribeAsync("all-stream", MetricSampleRequest.All,
                TimeSpan.FromSeconds(1), cancellation.Token).GetAsyncEnumerator();
            var pending = mode == 0 ? stream.MoveNextAsync().AsTask()
                : mode == 1 ? ((IMetricSnapshotPushSource)sampler).FetchOnceAsync("all-fetch", MetricSampleRequest.All, cancellation.Token)
                : (Task)sampler.GetSnapshotAsync(cancellation.Token);
            var plan = tracker.CreatePlan(DateTimeOffset.UtcNow);
            Assert.True(plan.DueItemIds.Count > 1024);
            tracker.MarkSampled(plan.Request!, DateTimeOffset.UtcNow);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.Empty(tracker.CreatePlan(DateTimeOffset.UtcNow).ActiveItemIds);
        }
    }

    [Fact]
    public async Task CapturedPlanCallbackCannotAllocateHistoryAfterStop()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture(
            HostManagerTestPlanFactory.CreatePlan() with { DataHistory = CompiledDataHistoryPlan.Compile([]) });
        var provider = subscriptions.Provider;
        var next = provider.Current with
        {
            Version = provider.Current.Version + 1,
            HostManager = provider.Current.HostManager with
            {
                DataHistory = CompiledDataHistoryPlan.Compile([new("cpu.usage", 4)])
            }
        };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.Published += plan =>
        {
            if (!ReferenceEquals(plan, next)) return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        using var sampler = CreateSampler(subscriptions, new HardwareMetricCatalogState());
        var owner = Assert.IsType<LastSuccessfulHardwareMetricSnapshot>(typeof(WindowsHardwareMetricSampler)
            .GetField("publishedSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
        var histories = Assert.IsAssignableFrom<System.Collections.IDictionary>(typeof(LastSuccessfulHardwareMetricSnapshot)
            .GetField("histories", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner));
        await sampler.StartAsync(CancellationToken.None);
        var publication = Task.Run(() => provider.Publish(next));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await sampler.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(histories);
        }
        finally
        {
            release.Set();
            Assert.False((await publication.WaitAsync(TimeSpan.FromSeconds(10))).HasDeliveryFailures);
            await sampler.StopAsync(CancellationToken.None);
        }
        Assert.Empty(histories);
    }

    [Fact]
    public async Task MeasurementSubscriptionPublishesChangedTopologyWithoutCatalogDemand()
    {
        using var fixture = new MetricSnapshotOwnerTestFixture.Fixture(MetricSnapshotOwnerTestFixture.Topology([]));
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("No NUMA demand"),
            () => new MemoryMetrics(UsedBytes: 512, TotalBytes: 1024, UsagePercent: 50, IsUsageAvailable: true)
            {
                AvailableBytes = 512,
                ObservationStatus = SamplingObservationStatus.Current
            });
        using var sampler = CreateSampler(fixture.Slots, fixture.Catalog, fixture.Owner, memory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sampler.CaptureSnapshotAsync(MetricSampleRequest.CatalogProbe, cancellation.Token);
        var original = fixture.Catalog.Current!;
        var changes = 0;
        fixture.Catalog.Changed += () => Interlocked.Increment(ref changes);
        var request = MetricSampleRequest.ForIds(["memory.percent"]);
        Assert.False(request.IncludesCatalog);
        await sampler.StartAsync(cancellation.Token);
        try
        {
            await using var stream = sampler.SubscribeAsync("memory-viewer", request, TimeSpan.FromMilliseconds(100),
                cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            Assert.True(await stream.MoveNextAsync());
            fixture.Topology = MetricSnapshotOwnerTestFixture.Topology([new(7, "new-disk", 1) { SupportsNvmeHealth = true }]);
            fixture.Owner.RequestTopologyRefresh();
            do { Assert.True(await stream.MoveNextAsync()); }
            while (fixture.Catalog.Current!.WorkspaceIdentity == original.WorkspaceIdentity);

            var updated = fixture.Catalog.Current!;
            Assert.Equal(fixture.Owner.CurrentWorkspaceIdentity, updated.WorkspaceIdentity);
            var diskAddress = updated.FieldBindings.Values.First(binding => binding.DeviceIdentity == "new-disk").RequestAddress;
            Assert.Contains(diskAddress, fixture.Catalog.SelectSubscribable(MetricSampleRequest.ForIds([diskAddress])).Ids);
            Assert.Equal(SamplingObservationStatus.Current,
                sampler.ReadLatest(MetricSampleRequest.All)!.Datasets[SamplingDatasetIds.StorageInventory].Status);
            var topologyReads = fixture.TopologyReads;
            var catalogChanges = Volatile.Read(ref changes);
            Assert.True(await stream.MoveNextAsync());
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal(topologyReads, fixture.TopologyReads);
            Assert.Equal(catalogChanges, Volatile.Read(ref changes));
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task LatePlanCallbackCannotReplaceCurrentHistoryRequirements()
    {
        using var fixture = new MetricSnapshotOwnerTestFixture.Fixture(MetricSnapshotOwnerTestFixture.Topology([]));
        var subscriptions = fixture.Slots;
        var provider = subscriptions.Provider;
        var current = provider.Current;
        var older = current with
        {
            Version = current.Version + 1,
            HostManager = current.HostManager with
            {
                DataHistory = CompiledDataHistoryPlan.Compile([new("cpu.usage", 2)])
            }
        };
        var newer = older with
        {
            Version = older.Version + 1,
            HostManager = older.HostManager with
            {
                DataHistory = CompiledDataHistoryPlan.Compile([new("cpu.usage", 5)])
            }
        };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.Published += plan =>
        {
            if (ReferenceEquals(plan, older))
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        using var sampler = CreateSampler(subscriptions, new HardwareMetricCatalogState(), fixture.Owner);
        await sampler.StartAsync(CancellationToken.None);
        var delayed = Task.Run(() => provider.Publish(older));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            provider.Publish(newer);
        }
        finally
        {
            release.Set();
            await delayed.WaitAsync(TimeSpan.FromSeconds(10));
            await sampler.StopAsync(CancellationToken.None);
        }
        Assert.Same(newer, provider.Current);
        var owner = Assert.IsType<LastSuccessfulHardwareMetricSnapshot>(
            typeof(WindowsHardwareMetricSampler).GetField("publishedSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
        for (ulong round = 1; round <= 6; round++)
        {
            var raw = CreateCatalogSnapshot() with
            {
                Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
                {
                    ["cpu.usage"] = new("cpu.usage", SamplingObservationStatus.Current, round,
                        DateTimeOffset.UtcNow.UtcTicks, 1, 1, 1, round)
                }
            };
            owner.Publish(raw, MetricSampleRequest.ForIds(["cpu.usage"]));
        }
        Assert.Equal(5, owner.Read()!.History["cpu.usage"].Length);
    }

    [Fact]
    public async Task SubscribeAsync_WaitsForBackendPublicationBeforeFirstValue()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        var catalog = new HardwareMetricCatalogState();
        catalog.PublishCatalogProbe(CreateCatalogSnapshot());
        using var sampler = CreateSampler(subscriptions, catalog);
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = sampler.SubscribeAsync(
                "no-bootstrap",
                MetricSampleRequest.ForIds(["cpu.usage"]),
                TimeSpan.FromSeconds(1),
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var first = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(100);

        Assert.False(first.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await first);
    }

    [Fact]
    public void InventoryDemandSurvivesCatalogFilteringAndDueCaptureWithoutNumericProbe()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        var catalog = new HardwareMetricCatalogState();
        var gpuItem = new MetricReadingValue(Id: "gpu.0.usage", NumericValue: 0, Unit: "%", Percent: null);
        catalog.PublishCatalogProbe(CreateCatalogSnapshot() with
        { Items = new Dictionary<string, MetricReadingValue> { [gpuItem.Id] = gpuItem } });
        using var sampler = CreateSampler(subscriptions, catalog);
        var inventory = MetricSampleRequest.ForIds([SamplingDatasetIds.SystemGpuInventory]);
        Assert.Equal(inventory.CacheKey, catalog.SelectSubscribable(inventory).CacheKey);
        var request = Assert.IsType<MetricSampleRequest>(typeof(WindowsHardwareMetricSampler)
            .GetMethod("CreateMetricSampleRequest", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(sampler, [new[] { SamplingDatasetIds.SystemGpuInventory },
                Array.Empty<NativeItemSamplingSubscriptionSourceView<MetricSampleRequest>>() ]));
        Assert.Equal(inventory.CacheKey, request.CacheKey);
        Assert.Equal([SamplingDatasetIds.SystemGpuInventory], sampler.GetPublicationItemIds(request));
    }

    [Fact]
    public void GpuMetricPublicationNotificationsExcludeInternalInventoryDependency()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = CreateSampler(
            subscriptions,
            new HardwareMetricCatalogState());

        var publicationIds = sampler.GetPublicationItemIds(
                MetricSampleRequest.ForIds(["gpu.0.power"]))
            .ToArray();

        Assert.Equal(["gpu.0.power"], publicationIds);
        Assert.DoesNotContain(
            SamplingDatasetIds.SystemGpuInventory,
            publicationIds,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BoundCurrentIsDeliveredAtSubscriberTriggerForOppositeAddresses()
    {
        const string canonical = "recorded-memory-used-field";
        const string legacy = "memory.usage";
        var definition = MetricFieldRegistry.FindByName("memory_all_physical_used_bytes")!;
        var observedAt = DateTimeOffset.UtcNow;
        var frame = CreateCatalogSnapshot() with
        {
            WorkspaceIdentity = 1,
            ConfigurationGeneration = 1,
            FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            {
                [canonical] = new("recorded-memory", definition, canonical, "windows.memory") { RequestAliases = [legacy] }
            },
            Items = new Dictionary<string, MetricReadingValue>
            {
                [canonical] = new(Id: canonical, NumericValue: 512, Unit: "B", Percent: null),
                [legacy] = new(Id: legacy, NumericValue: 512, Unit: "B", Percent: null)
            },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
            {
                [canonical] = new(canonical, SamplingObservationStatus.Current, 1, observedAt.UtcTicks, 1, 1, 1, 1),
                [legacy] = new(legacy, SamplingObservationStatus.Current, 1, observedAt.UtcTicks, 1, 1, 1, 1)
            }
        };
        foreach (var (published, subscribed) in new[] { (legacy, canonical), (canonical, legacy) })
        {
            using var fixture = new MetricSnapshotOwnerTestFixture.Fixture(MetricSnapshotOwnerTestFixture.Topology([]));
            var subscriptions = fixture.Slots;
            var catalog = new HardwareMetricCatalogState();
            catalog.PublishCatalogProbe(frame);
            using var sampler = CreateSampler(subscriptions, catalog, fixture.Owner);
            var owner = Assert.IsType<LastSuccessfulHardwareMetricSnapshot>(typeof(WindowsHardwareMetricSampler)
                .GetField("publishedSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var stream = sampler.SubscribeAsync("bound-notification", MetricSampleRequest.ForIds([subscribed]),
                TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator(timeout.Token);
            var next = stream.MoveNextAsync().AsTask();
            Assert.False(next.IsCompleted);
            owner.Publish(frame, MetricSampleRequest.ForIds([published]));
            sampler.PublishCompletedDatasets([published]);
            Assert.Equal(0, owner.ComposedViewBuildCount);
            Assert.False(next.IsCompleted); // A dataset completion is not a subscription deadline.
            var tracker = Assert.IsType<NativeItemSamplingSubscriptionTracker<MetricSampleRequest>>(
                typeof(WindowsHardwareMetricSampler).GetField("subscriptions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
            var now = DateTimeOffset.UtcNow.AddSeconds(1);
            var plan = tracker.CreatePlan(now);
            tracker.CompletePlan(plan, now, ResourceManager.Subscription.Native.NativeSamplingSubscriptionCompletionStatus.Sampled);
            var delivery = Assert.IsType<ResourceManager.Subscription.NativeCurrentValueSubscriptionDelivery<HardwareMetricSnapshot>>(
                typeof(WindowsHardwareMetricSampler).GetField("snapshotSubscriptionDelivery", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
            delivery.Deliver(plan.Triggers);
            Assert.True(await next.WaitAsync(timeout.Token));
            Assert.Equal(512d, stream.Current.Items[subscribed].NumericValue);
            Assert.Equal(0, owner.ComposedViewBuildCount);
            Assert.Equal(1, owner.ProjectionBuildCount);
            Assert.Equal(512d, owner.Read()!.Items[canonical].NumericValue);
        }
    }

    [Fact]
    public async Task SnapshotSubscribersHaveIndependentDeliveryIntervalsWhileSharingSampling()
    {
        using var fixture = new MetricSnapshotOwnerTestFixture.Fixture(MetricSnapshotOwnerTestFixture.Topology([]));
        var reads = 0;
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("No NUMA demand"),
            () => new MemoryMetrics((ulong)Interlocked.Increment(ref reads), 10000, 50, true)
            { ObservationStatus = SamplingObservationStatus.Current });
        using var sampler = CreateSampler(fixture.Slots, fixture.Catalog, fixture.Owner, memory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sampler.CaptureSnapshotAsync(MetricSampleRequest.CatalogProbe, timeout.Token);
        await sampler.StartAsync(timeout.Token);
        try
        {
            var request = MetricSampleRequest.ForIds(["memory.percent"]);
            async Task Observe(string id, int interval, int count)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await using var stream = sampler.SubscribeAsync(id, request, TimeSpan.FromMilliseconds(interval), timeout.Token)
                    .GetAsyncEnumerator(timeout.Token);
                for (var index = 1; index <= count; index++)
                {
                    Assert.True(await stream.MoveNextAsync());
                    Assert.True(clock.Elapsed.TotalMilliseconds >= interval * index - 80,
                        $"{id} delivered frame {index} at {clock.Elapsed.TotalMilliseconds} ms");
                    Assert.NotNull(stream.Current.Items["memory.percent"].NumericValue);
                }
            }
            await Task.WhenAll(Observe("fast", 250, 6), Observe("slow", 750, 2));
            Assert.InRange(Volatile.Read(ref reads), 4, 10);
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task DuplicateStartIsIdempotentAndStoppedInstanceCanRestart()
    {
        using var fixture = new MetricSnapshotOwnerTestFixture.Fixture(MetricSnapshotOwnerTestFixture.Topology([]));
        using var sampler = CreateSampler(fixture.Slots, fixture.Catalog, fixture.Owner);

        await sampler.StartAsync(CancellationToken.None);
        var worker = typeof(WindowsHardwareMetricSampler).GetField("samplingWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
        Assert.Equal("ResourceManager.SubscriptionCore", worker.GetType().Assembly.GetName().Name);
        var first = Assert.IsAssignableFrom<Task>(
            sampler.CaptureSamplingWorkerTask());

        await sampler.StartAsync(CancellationToken.None);
        if (first.IsFaulted) await first; // Preserve the actual worker exception in the test receipt.
        Assert.Same(first, sampler.CaptureSamplingWorkerTask());

        await sampler.StopAsync(CancellationToken.None);
        if (first.IsFaulted) await first;
        Assert.Null(sampler.CaptureSamplingWorkerTask());

        await sampler.StartAsync(CancellationToken.None);
        var restarted = Assert.IsAssignableFrom<Task>(
            sampler.CaptureSamplingWorkerTask());
        Assert.NotSame(first, restarted);

        await sampler.StopAsync(CancellationToken.None);
        if (restarted.IsFaulted) await restarted;
        Assert.Null(sampler.CaptureSamplingWorkerTask());
    }

    [Fact]
    public async Task FreshProjectedEngineUsesInternalQueryReceiptAndPublicDeliveryItem()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = CreateSampler(subscriptions, new HardwareMetricCatalogState());
        var backend = new FieldSubscriptionSamplingModeRecordedBorrow();
        var field = backend.QueryA;
        const string publicAddress = "gpu-engine/recorded/0/1/busyPercent";
        const string triggerAddress = "gpu.0.engineUsage";
        var binding = new MetricDeviceFieldBinding("recorded-gpu",
            MetricFieldRegistry.FindByName("gpu_all_engine_busy_percent")!, publicAddress, "pdh.gpu.engine")
            { SampleRequestAddress = triggerAddress };
        var selection = new ResourceManager.ReadWrite.Fields.MetricFieldSelection(binding,
            MetricSampleRequest.ForIds([publicAddress]));
        var bindings = Assert.IsType<System.Collections.Immutable.ImmutableArray<ResourceManager.ReadWrite.FieldCollectionBinding>>(
            typeof(WindowsHardwareMetricSampler).GetMethod("CreateFieldCollectionBindings",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(sampler,
                    [System.Collections.Immutable.ImmutableArray.Create(field), new[] { selection }]));
        var delivery = new ResourceManager.Subscription.NativeFieldSubscriptionDelivery();
        using var registration = delivery.Register("engine-fresh", backend, [field], bindings);
        backend.SetCurrent(field, "fresh-engine-27.5");
        System.Collections.Immutable.ImmutableArray<ResourceManager.Subscription.NativeItemSamplingSubscriptionTrigger<string>> triggers =
            [new("engine-fresh", publicAddress, 100, 1, "recorded", IsFresh: true)];
        var result = delivery.CaptureCompleted(triggers, new HashSet<string>([triggerAddress]), committed: true);
        Assert.Equal(new[] { field }, result.QueriedFields.ToArray());
        delivery.Deliver(triggers, result);
        await using var reader = registration.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("fresh-engine-27.5", Assert.Single(reader.Current).Value!.Text);
        Assert.Equal(ResourceManager.ReadWrite.FieldObservationStatus.Current, Assert.Single(reader.Current).Status);
    }

    [Fact]
    public async Task FinalDisposeClearsOutstandingRegistrationsClosesStreamsAndIgnoresLateLeaseReturns()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = CreateSampler(subscriptions, AllMetricSubscriptionTestData.RecordedCatalog());
        var request = MetricSampleRequest.All;
        using var lease = sampler.AcquireSubscription("dispose-outstanding", request, TimeSpan.FromSeconds(1));
        await using var stream = sampler.SubscribeAsync("dispose-stream", request, TimeSpan.FromSeconds(1),
            CancellationToken.None).GetAsyncEnumerator();
        var pending = stream.MoveNextAsync().AsTask();
        var tracker = Assert.IsType<NativeItemSamplingSubscriptionTracker<MetricSampleRequest>>(
            typeof(WindowsHardwareMetricSampler).GetField("subscriptions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
        Assert.Equal((2, 2), tracker.CaptureManagedSourceState());
        Assert.True(subscriptions.Owner.Activity.HasInUseSubscriptions(1));

        sampler.Dispose();

        Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
        Assert.False(subscriptions.Owner.Activity.HasInUseSubscriptions(1));
        Assert.Equal(0U, subscriptions.Owner.Execute(1, session =>
        {
            var capacity = session.Capacity;
            var header = new NativeSamplingSubscriptionSnapshotHeader
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeSamplingSubscriptionSnapshotHeader>()
            };
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header,
                new NativeSamplingSubscriptionSourceView[checked((int)capacity.SnapshotSourceCapacity)],
                new NativeSamplingSubscriptionItemState[checked((int)capacity.SnapshotItemCapacity)]));
            return header.ActiveSourceCount;
        }));
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(Record.Exception(lease.Dispose));
        Assert.Null(Record.Exception(lease.Dispose));
        Assert.Null(Record.Exception(sampler.Dispose));
        Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
    }

    [Fact]
    public void FinalDisposeRetriesSubscriptionClearBeforeClosingRemainingStorage()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = CreateSampler(subscriptions, AllMetricSubscriptionTestData.RecordedCatalog());
        using var lease = sampler.AcquireSubscription("clear-pending", MetricSampleRequest.All, TimeSpan.FromSeconds(1));
        var tracker = Assert.IsType<NativeItemSamplingSubscriptionTracker<MetricSampleRequest>>(
            typeof(WindowsHardwareMetricSampler).GetField("subscriptions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
        var plan = tracker.CreatePlan(DateTimeOffset.UtcNow);
        Assert.NotNull(plan.Request);
        try
        {
            Assert.Throws<InvalidOperationException>(sampler.Dispose);
            Assert.Equal(0, typeof(WindowsHardwareMetricSampler)
                .GetField("ownedResourcesDisposed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
            var gate = Assert.IsType<SemaphoreSlim>(typeof(WindowsHardwareMetricSampler)
                .GetField("sampleGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
            Assert.True(gate.Wait(0)); // A failed clear must not close later storage.
            gate.Release();
        }
        finally { tracker.MarkSampled(plan.Request!, DateTimeOffset.UtcNow); }

        sampler.Dispose();
        Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
        Assert.False(subscriptions.Owner.Activity.HasInUseSubscriptions(1));
        Assert.Null(Record.Exception(lease.Dispose));
        Assert.Null(Record.Exception(sampler.Dispose));
    }

    internal static WindowsHardwareMetricSampler CreateSampler(
        HostManagerSamplingSubscriptionTestFixture subscriptions,
        HardwareMetricCatalogState catalog,
        HostManagerMetricSnapshotOwner? owner = null,
        WindowsMemoryMonitoringZone? memory = null)
        => new(
            cpuZone: null!,
            memoryZone: memory!,
            virtualMemoryZone: null!,
            gpuAdapterOrderZone: null!,
            cpuFrequencyZone: null!,
            gpuEngineZone: null!,
            systemIoZone: null!,
            nvidiaZone: null!,
            nvidiaNvapiZone: null!,
            amdAdlxZone: null!,
            amdSmuZone: null!,
            integratedGpuSensorRoute: new AmdIntegratedGpuSensorRoute(),
            platformSensorReader: null!,
            metricSnapshotOwner: owner!,
            metricCatalog: catalog,

            samplingSubscriptionOwner: subscriptions.Owner,
            runtimePlanProvider: subscriptions.Provider,
            logger: NullLogger<WindowsHardwareMetricSampler>.Instance);

    private static HardwareMetricSnapshot CreateCatalogSnapshot()
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
                    new HardwareSensorProviderState("test", HardwareSensorProviderStates.Unavailable, null),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsUsageAvailable: false),
            new VirtualMemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false),
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
            new Dictionary<string, MetricReadingValue>());
    }
}
