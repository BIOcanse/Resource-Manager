using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class ControlCatalogLockTests
{
    [Fact]
    public async Task EndedFreshSourceReturnsEmptyCatalogWithoutReadingRetainedObservations()
    {
        var metrics = new EndedMetrics();
        var catalog = new WindowsControlObjectCatalog(new HardwareMetricCatalogState(), [], new Access("normal"), null!, null!,
            new ForbiddenObservations(), metrics);
        var result = await catalog.ReadObjectsAsync(CancellationToken.None);
        Assert.Empty(result.Objects);
        Assert.Equal("control-object-catalog:topology", metrics.Holder);
        Assert.True(metrics.Request!.IsCatalogProbe);
        Assert.False(metrics.Request.IsAll);
    }

    [Fact]
    public async Task KnownTopologyRequestsOnlyInventoryAndConsumedFanFieldsWithoutCachedFallback()
    {
        const string fanAddress = "recorded-gpu/fan-rpm";
        var fan = new MetricDeviceFieldBinding("recorded-gpu", MetricFieldRegistry.FindByName(
            "gpu_all_fan_speed_reported_rpm")!, fanAddress, "recorded");
        var unrelated = new MetricDeviceFieldBinding("recorded-gpu", MetricFieldRegistry.FindByName(
            "gpu_all_busiest_engine_busy_percent")!, "recorded-gpu/unrelated", "recorded");
        var topology = new HardwareMetricCatalog(new("Recorded CPU", 0, "recorded"),
            [new(2, "Recorded GPU", "recorded-gpu", 2, "recorded")],
            new Dictionary<string, HardwareMetricItemDescriptor>(),
            new Dictionary<string, MetricDeviceFieldBinding> { [fanAddress] = fan, [unrelated.RequestAddress] = unrelated },
            1, 1, 1);
        var state = new HardwareMetricCatalogState();
        state.PublishCatalogProbe(topology.CreateSnapshot());
        var metrics = new EndedMetrics();
        var catalog = new WindowsControlObjectCatalog(state, [], new Access("normal"), null!, null!,
            new ForbiddenObservations(), metrics);

        var result = await catalog.ReadObjectsAsync(CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.Equal("control-object-catalog", metrics.Holder);
        Assert.False(metrics.Request!.IsAll);
        Assert.True(metrics.Request.IncludesCatalog);
        Assert.Equal(new[] { SamplingDatasetIds.SystemGpuInventory, "cpu.fanRpm", "gpu.2.fanRpm", fanAddress }
            .Order(StringComparer.Ordinal), metrics.Request.Ids.Order(StringComparer.Ordinal));
        Assert.False(metrics.Request.Includes(unrelated.RequestAddress));
    }

    private sealed class EndedMetrics : ResourceManager.App.Application.Metrics.IMetricSnapshotPushSource
    {
        internal string? Holder;
        internal MetricSampleRequest? Request;
        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string id, MetricSampleRequest request,
            TimeSpan interval, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Holder = id;
            Request = request;
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ForbiddenObservations : ResourceManager.App.Application.Metrics.IMetricSnapshotObservationSource
    {
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request)
            => throw new InvalidOperationException("A fresh read must not fall back to retained observations.");
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval)
            => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("root")]
    public void MissingWriterIsNeverAdvertisedAsAnAccessLevelFix(string level)
    {
        var catalog = new WindowsControlObjectCatalog(null!, [], new Access(level), null!, null!, new EmptyObservations(), null!);
        var capability = new ControlCapability("gpu.core-voltage-offset", ControlValueKinds.Number,
            false, "control-writer-not-implemented", ControlUnavailableKinds.NotImplemented, RequiredAccessLevel: ControlAccessLevels.Root);
        var target = new ControlObject("gpu:test", ControlObjectKinds.Gpu, "GPU",
            new(ControlOperatingSystems.Windows, ControlVendors.Nvidia), [capability]);
        var result = catalog.ResolveCapability(target, capability);
        Assert.Equal(ControlUnavailableKinds.NotImplemented, result.UnavailableKind);
        Assert.Equal("control-writer-not-implemented", result.UnavailableReason?.Code);
        Assert.False(result.Supported);
    }

    private sealed class Access(string current) : IControlAccessLevel
    {
        public string Current => current;
        public Task SetAsync(string level, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyObservations : ResourceManager.App.Application.Metrics.IMetricSnapshotObservationSource
    {
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => null;
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval)
            => throw new NotSupportedException();
    }
}
