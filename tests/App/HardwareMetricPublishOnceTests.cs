using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareMetricPublishOnceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservedWordsInLegacyKeysCannotAliasDistinctPublishedViews(bool includeCpu)
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        var frame = Frame();
        var items = frame.Items.ToDictionary();
        items["catalog"] = new("catalog", 17, "count");
        var datasets = frame.Datasets.ToDictionary();
        datasets["catalog"] = frame.Datasets["cpu.usage"] with { DatasetId = "catalog" };
        datasets[MetricDatasetIds.SystemMetricCatalog] = frame.Datasets["cpu.usage"] with { DatasetId = MetricDatasetIds.SystemMetricCatalog };
        owner.Publish(frame with { Items = items, Datasets = datasets }, MetricSampleRequest.All);
        var fields = MetricSampleRequest.ForIds(includeCpu ? ["catalog", "cpu.usage"] : ["catalog"]);
        var metadata = includeCpu ? MetricSampleRequest.ForIds(["cpu.usage"]).WithCatalog() : MetricSampleRequest.CatalogProbe;
        Assert.Equal(fields.CacheKey, metadata.CacheKey);
        var catalogView = owner.Read(metadata)!;
        var fieldView = owner.Read(fields)!;
        Assert.Equal(includeCpu ? 1 : 0, catalogView.Items.Count);
        Assert.Equal(includeCpu ? 2 : 1, fieldView.Items.Count);
        Assert.Equal(17, fieldView.Items["catalog"].NumericValue);
        Assert.NotSame(catalogView, fieldView);
        Assert.Same(fieldView, owner.Read(fields));
        Assert.Same(catalogView, owner.Read(metadata));
    }

    [Fact]
    public void DirectPublicationKeepsOwnerHistoryAndOwnsGpuCollections()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.ConfigureHistory([new("cpu.usage", 4)]);
        var frame = Frame();
        owner.Publish(frame, MetricSampleRequest.All);
        var gpus = new List<GpuMetrics>
        {
            new(0, "recorded", 42, 0, 0, 0, 0, 0, 0, 0,
                new(new("recorded", "current", null), null, null, null, null, null, null, null, null, null, null), "recorded", true, "recorded")
        };
        var adapters = new List<SchedulingGpuAdapterObservation>();
        owner.Publish(frame with { Datasets = ImmutableDictionary<string, HardwareMetricDatasetObservation>.Empty,
            Gpus = gpus, GpuInventory = frame.GpuInventory with { Adapters = adapters } });
        var direct = owner.Read()!;
        gpus.Clear();
        Assert.Single(direct.Gpus);
        Assert.Equal(42, Assert.Single(direct.History["cpu.usage"]).Value);
        Assert.Throws<NotSupportedException>(() => ((IList<GpuMetrics>)direct.Gpus).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<SchedulingGpuAdapterObservation>)direct.GpuInventory.Adapters).Clear());
        owner.ConfigureHistory([new("cpu.usage", 2)]);
        Assert.Single(owner.Read()!.Gpus);
        Assert.Equal(42, Assert.Single(owner.Read()!.History["cpu.usage"]).Value);
    }

    [Theory]
    [InlineData(SamplingObservationStatus.Unavailable)]
    [InlineData(SamplingObservationStatus.Unsupported)]
    [InlineData(SamplingObservationStatus.Warming)]
    [InlineData(SamplingObservationStatus.Partial)]
    [InlineData(SamplingObservationStatus.Invalid)]
    [InlineData(SamplingObservationStatus.RetainedLastGood)]
    public void FailedCandidateRetainsOnlyTheOwnersGoodValueAndRecordsANullHistoryRound(SamplingObservationStatus status)
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.ConfigureHistory([new("cpu.usage", 4)]);
        var frame = Frame();
        var request = MetricSampleRequest.ForIds(["cpu.usage"]);
        owner.Publish(frame, MetricSampleRequest.All);
        var first = owner.Read()!;
        var at = frame.CapturedAt.AddSeconds(2);
        var failed = frame with
        {
            CapturedAt = at,
            Cpu = frame.Cpu with { UsagePercent = 99 },
            Items = new Dictionary<string, MetricReadingValue> { ["cpu.usage"] = new("cpu.usage", 99, "%") },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
            {
                ["cpu.usage"] = frame.Datasets["cpu.usage"] with { Status = status,
                    SourceGeneration = 99, ObservedAtUtcTicks = at.UtcTicks, LastAttemptAtUtcTicks = at.UtcTicks, FailureCode = "recorded-failure" }
            }
        };
        Assert.False(owner.Publish(failed, request).IsSuccessful);
        var current = owner.Read()!;
        Assert.Equal(42, current.Cpu.UsagePercent);
        Assert.Same(first.Items["cpu.usage"], current.Items["cpu.usage"]);
        Assert.Same(first.Items["memory.usage"], current.Items["memory.usage"]);
        var observation = current.Datasets["cpu.usage"];
        Assert.Equal(status == SamplingObservationStatus.RetainedLastGood ? SamplingObservationStatus.Unavailable : status, observation.Status);
        Assert.Equal(first.Datasets["cpu.usage"].ObservedAtUtcTicks, observation.ObservedAtUtcTicks);
        Assert.Equal(first.Datasets["cpu.usage"].LastSuccessAtUtcTicks, observation.LastSuccessAtUtcTicks);
        Assert.Equal(1UL, observation.SourceGeneration);
        Assert.Equal(at.UtcTicks, observation.LastAttemptAtUtcTicks);
        Assert.Equal("recorded-failure", observation.FailureCode);
        Assert.Equal(new double?[] { 42, null }, current.History["cpu.usage"].Select(static sample => sample.Value));
        Assert.Equal(SamplingObservationStatus.Current, first.Datasets["cpu.usage"].Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitInvalidationClearsOnlyItsSlotEvenOutsideTheRequestAndFailureCannotResurrectIt(bool omitObservation)
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.ConfigureHistory([new("cpu.usage", 4)]);
        var frame = Frame();
        owner.Publish(frame, MetricSampleRequest.All);
        var before = owner.Read()!;
        var at = frame.CapturedAt.AddSeconds(1);
        var invalidated = frame with
        {
            CapturedAt = at,
            InvalidatedDatasetIds = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "cpu.usage"),
            Datasets = frame.Datasets.ToDictionary(static pair => pair.Key, pair => pair.Value with
            {
                Status = SamplingObservationStatus.Unavailable,
                LastAttemptAtUtcTicks = at.UtcTicks
            })
        };
        if (omitObservation) invalidated = invalidated with
        {
            Datasets = invalidated.Datasets.Where(static pair => pair.Key != "cpu.usage").ToDictionary()
        };
        owner.Publish(invalidated, MetricSampleRequest.ForIds(["memory.usage"]));
        var current = owner.Read()!;
        Assert.DoesNotContain("cpu.usage", current.Items.Keys);
        Assert.Equal(SamplingObservationStatus.Unavailable, current.Datasets["cpu.usage"].Status);
        Assert.Equal(0, current.Datasets["cpu.usage"].LastSuccessAtUtcTicks);
        Assert.Same(before.Items["memory.usage"], current.Items["memory.usage"]);
        Assert.Equal(SamplingObservationStatus.Unavailable, current.Datasets["memory.usage"].Status);
        Assert.Same(before.Items["cpu.frequency"], current.Items["cpu.frequency"]);
        Assert.Equal(SamplingObservationStatus.Current, current.Datasets["cpu.frequency"].Status);
        Assert.Equal(new double?[] { 42, null }, current.History["cpu.usage"].Select(static sample => sample.Value));
        owner.RecordFailure(MetricSampleRequest.ForIds(["cpu.usage"]), at.AddSeconds(1), "read-failed");
        Assert.DoesNotContain("cpu.usage", owner.Read()!.Items.Keys);
        owner.Publish(frame, MetricSampleRequest.ForIds(["cpu.usage"]));
        Assert.Equal(42, owner.Read()!.Items["cpu.usage"].NumericValue);
        Assert.Equal(42, before.Items["cpu.usage"].NumericValue);
    }

    [Fact]
    public async Task ExplicitSourceInvalidationWakesAnUnrequestedNpuEngineSubscriber()
    {
        var recording = new NpuEngineRecordedResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Create(), new(),
            unavailableCooldown: TimeSpan.Zero);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var catalog = await owner.CaptureAsync(MetricSampleRequest.CatalogProbe, timeout.Token);
        var bindings = catalog.FieldBindings.Values.Where(static binding => binding.NpuEngine is not null).ToArray();
        await owner.CaptureAsync(MetricSampleRequest.ForIds(bindings.Select(static binding => binding.RequestAddress)), timeout.Token);
        var selected = bindings[0];
        var unrequested = bindings.First(binding => binding.DeviceIdentity != selected.DeviceIdentity);
        using var subscription = owner.Publications.Subscribe([unrequested.RequestAddress]);
        recording.InvalidateDuringRead = true;
        var invalidated = await owner.CaptureAsync(MetricSampleRequest.ForIds([selected.RequestAddress]), timeout.Token);
        await subscription.WaitAsync(timeout.Token);
        Assert.All(bindings, binding =>
        {
            Assert.DoesNotContain(binding.RequestAddress, invalidated.Items.Keys);
            Assert.Equal(SamplingObservationStatus.Unavailable, invalidated.Datasets[binding.RequestAddress].Status);
        });
    }

    [Fact]
    public async Task TwoConcurrentReadersShareOneComposedObjectPerVersion()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.Publish(Frame(), MetricSampleRequest.All);
        Assert.Equal(0, owner.ComposedViewBuildCount);
        var readers = await Task.WhenAll(Task.Run(() => owner.Read()), Task.Run(() => owner.Read()));
        Assert.Same(readers[0], readers[1]);
        Assert.Equal(1, owner.ComposedViewBuildCount);
        var first = readers[0]!;
        owner.Publish(Frame() with { Cpu = Frame().Cpu with { UsagePercent = 73 } }, MetricSampleRequest.ForIds(["cpu.usage"]));
        Assert.Equal(1, owner.ComposedViewBuildCount);
        var second = owner.Read()!;
        Assert.NotSame(first, second);
        Assert.Same(second, owner.Read());
        Assert.Equal(42, first.Cpu.UsagePercent);
        Assert.Equal(73, second.Cpu.UsagePercent);
        Assert.Same(first.Items["memory.usage"], second.Items["memory.usage"]);
        Assert.Equal(2, owner.ComposedViewBuildCount);
    }

    [Fact]
    public void UpdatesAndHistoryDoNotOpenNativeCellsOrBuildTheComposedView()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot("recorded", () => throw new Xunit.Sdk.XunitException("No native cell reader exists."));
        owner.ConfigureHistory([new("cpu.usage", 4)]);
        owner.Publish(Frame(), MetricSampleRequest.All);
        owner.Publish(Frame(), MetricSampleRequest.ForIds(["cpu.usage"]));
        owner.RecordFailure(MetricSampleRequest.ForIds(["memory.usage"]), DateTimeOffset.UnixEpoch.AddSeconds(11), "recorded-error");
        owner.ConfigureHistory([new("cpu.usage", 2)]);
        var metadata = owner.ReadPublicationContext();
        Assert.NotNull(metadata);
        Assert.Same(metadata, owner.ReadPublicationContext());
        Assert.Empty(metadata.Items);
        Assert.Equal(0, owner.ComposedViewBuildCount);
        Assert.Equal(0, owner.ProjectionBuildCount);
        Assert.Equal(2, owner.Read()!.History["cpu.usage"].Length);
    }

    [Fact]
    public void EquivalentRequestsShareTheirVersionProjectionWithoutPerReadAllocations()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.Publish(Frame(), MetricSampleRequest.All);
        var request = MetricSampleRequest.ForIds(["cpu.usage"]);
        var projection = owner.Read(request)!;
        Assert.Same(projection, owner.Read(MetricSampleRequest.ForIds(["CPU.USAGE"])));
        Assert.Single(projection.Items);
        Assert.Equal(0, owner.ComposedViewBuildCount);
        Assert.Equal(1, owner.ProjectionBuildCount);
        ImmutableArray<string> fields = ["cpu.usage"];
        var items = owner.ReadItems(fields);
        Assert.Same(items, owner.ReadItems(["CPU.USAGE"]));
        Assert.Equal(0, Allocated(() => owner.Read(request)));
        Assert.Equal(0, Allocated(() => owner.ReadItems(fields)));
        var before = owner.Read()!;
        Assert.Equal(0, Allocated(() => owner.Read()));
        owner.Publish(Frame(), request);
        Assert.NotSame(projection, owner.Read(request));
        Assert.NotSame(before, owner.Read());
    }

    [Fact]
    public void ProducerAndReaderCannotMutatePublishedCollectionsOrBytes()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        var bytes = new byte[] { 1, 2, 3 };
        var aliases = new[] { "cpu.usage" };
        var processors = new[] { new MetricProcessorAddress(0, 0) };
        var binding = new MetricDeviceFieldBinding("recorded", MetricFieldRegistry.FindByName("cpu_all_usage_percent")!, "cpu.usage", "recorded")
        {
            RequestAliases = aliases,
            Device = new("cpu", new() { ProcessorAddresses = processors }, "recorded", 0)
        };
        var frame = Frame();
        var items = frame.Items.ToDictionary();
        items["cpu.usage"] = items["cpu.usage"] with { BytesValue = bytes };
        var bindings = new Dictionary<string, MetricDeviceFieldBinding> { ["cpu.usage"] = binding };
        owner.Publish(frame with { Items = items, FieldBindings = bindings }, MetricSampleRequest.All);
        var published = owner.Read()!;
        bytes[0] = 9; aliases[0] = "changed"; processors[0] = new(9, 9);
        items.Clear(); bindings.Clear();
        Assert.Equal(new byte[] { 1, 2, 3 }, published.Items["cpu.usage"].BytesValue);
        published.Items["cpu.usage"].BytesValue![0] = 8;
        Assert.Equal(1, published.Items["cpu.usage"].BytesValue![0]);
        Assert.Equal("cpu.usage", published.FieldBindings["cpu.usage"].RequestAliases[0]);
        Assert.Equal(new MetricProcessorAddress(0, 0), published.FieldBindings["cpu.usage"].Device.Address!.ProcessorAddresses![0]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, MetricReadingValue>)published.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, HardwareMetricDatasetObservation>)published.Datasets).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)published.FieldBindings["cpu.usage"].RequestAliases)[0] = "changed");
    }

    [Fact]
    public void AllocationMeasurement()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        var frame = Frame();
        var request = MetricSampleRequest.ForIds(["cpu.usage"]);
        ImmutableArray<string> ids = ["cpu.usage"];
        owner.Publish(frame, MetricSampleRequest.All);
        for (var i = 0; i < 16; i++)
        {
            owner.Publish(frame, request);
            _ = owner.Read(); _ = owner.Read(request); _ = owner.ReadItems(ids);
        }
        var updateBytes = Allocated(() => owner.Publish(frame, request));
        _ = owner.Read(); _ = owner.Read(request); _ = owner.ReadItems(ids);
        var result = new Dictionary<string, long>
        {
            ["update"] = updateBytes,
            ["read"] = Allocated(() => owner.Read()),
            ["requestRead"] = Allocated(() => owner.Read(request)),
            ["itemsRead"] = Allocated(() => owner.ReadItems(ids))
        };
        Assert.Equal(0, result["read"]);
        Assert.Equal(0, result["requestRead"]);
        Assert.Equal(0, result["itemsRead"]);
        output.WriteLine(JsonSerializer.Serialize(result));
        var path = Environment.GetEnvironmentVariable("RM_PUBLISH_ONCE_ALLOCATIONS");
        if (path is not null) File.WriteAllText(path, JsonSerializer.Serialize(result));
    }

    private static long Allocated(Action action)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 64; i++) action();
        return (GC.GetAllocatedBytesForCurrentThread() - start) / 64;
    }

    private static HardwareMetricSnapshot Frame()
    {
        var at = DateTimeOffset.UnixEpoch.AddSeconds(10);
        string[] ids = ["cpu.usage", "memory.usage", "virtualMemory.usage", "cpu.frequency"];
        return new(at, HardwareMetricTestData.EmptyCpu with { UsagePercent = 42, IsUsageAvailable = true,
                ObservationStatus = CpuMetricObservationStatus.Complete },
            new(400, 800, 50, true), new(500, 1000, 50, true), [], HardwareMetricTestData.EmptyGpuInventory,
            ids.ToDictionary(static id => id, id => new MetricReadingValue(id, 42, "%") { ObservedAt = at }))
        {
            Datasets = ids.ToDictionary(static id => id, id => new HardwareMetricDatasetObservation(id,
                SamplingObservationStatus.Current, 1, at.UtcTicks, 1, 1, 1, 1))
        };
    }
}
