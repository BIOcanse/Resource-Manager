using System.Text.Json;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class GpuMemoryResourceFactTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void CatalogCapabilityIsAnOwnerFactNotAValueOrNameInference(bool? capability)
    {
        var hardware = Hardware(capability);
        var catalog = HardwareMetricCatalog.FromSnapshot(hardware);
        Assert.Equal(capability, Assert.Single(catalog.Gpus).HasDedicatedMemoryMetrics);
        var current = catalog.CreateSnapshot();
        Assert.Equal(capability, Assert.Single(current.Gpus).HasDedicatedMemoryMetrics);
        var parts = MetricCatalog.FromSnapshot(current).Where(metric => metric.Id.StartsWith("gpu.1.")).ToArray();
        Assert.NotEmpty(parts);
        Assert.All(parts, part => Assert.Equal(capability, part.HasDedicatedMemoryMetrics));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(parts, options));
        Assert.All(json.RootElement.EnumerateArray(), part => Assert.Equal(capability,
            part.GetProperty("hasDedicatedMemoryMetrics").ValueKind == JsonValueKind.Null ? (bool?)null : part.GetProperty("hasDedicatedMemoryMetrics").GetBoolean()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeCurrentPublicationPreservesCapabilityWithoutMemorySampling(bool capability)
    {
        var source = Hardware(capability);
        const string id = "gpu.1.usage";
        var dataset = SamplingDatasetIds.ForSystemMetric(id);
        source = source with
        {
            Items = new Dictionary<string, MetricReadingValue> { [id] = new(id, 10, "%") },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
            { [dataset] = new(dataset, SamplingObservationStatus.Current, 1, source.CapturedAt.UtcTicks, 1, 1, 1, 1) }
        };
        using var state = new LastSuccessfulHardwareMetricSnapshot();
        state.Publish(source, MetricSampleRequest.ForIds([id]));
        Assert.Equal(capability, Assert.Single(state.Read()!.Gpus).HasDedicatedMemoryMetrics);
    }

    [Fact]
    public void IntegratedGpuDoesNotUseAllocationOwnershipAsADedicatedMemoryTotal()
    {
        var bar = WindowsResourceBreakdownSampler.CreateBar("gpu.1.vram", "capacity", null!, null!,
            Hardware(false), ProcessGpuBreakdownRead.NotRequested, null!, null!, null, new StaticResourceResidualBreakdownProvider())!;
        Assert.Null(bar.TotalValue);
        Assert.Null(bar.CapacityValue);
        Assert.Null(bar.TotalSystemPercent);
        Assert.Empty(bar.Software);
        Assert.Equal("dedicated_memory_used", bar.Quantity);
        Assert.Equal("gpu-dedicated-memory-metrics-unsupported", bar.UnavailableReason);
    }

    [Fact]
    public void SharedOnlyMemoryWithoutDedicatedTotalPublishesExplicitNullsAndNoNumericRows()
    {
        var snapshot = SharedOnlySnapshot();
        var facts = ResourceFactsSnapshot.Create(snapshot, ["gpu.1.vram"], ResourceFactsPublicationTests.Foundations());
        var metric = Assert.Single(facts.Metrics);
        Assert.Equal("dedicated_memory_used", metric.Quantity);
        Assert.Equal("gpu-dedicated-memory-total-unavailable", metric.UnavailableReason);
        Assert.Null(metric.TotalValue); Assert.Null(metric.CapacityValue); Assert.Null(metric.TotalSystemPercent);
        Assert.Null(metric.ProcessSumValue); Assert.Null(metric.ResidualValue); Assert.Null(metric.SharedValue);
        Assert.Empty(metric.Software);
        // Shape only: the continuity owner decides which requested bars must exist.
        var projected = Assert.Single(ResourceBreakdownWireSnapshot.ProjectResourceBars(snapshot, ["gpu.1.vram"], null));
        Assert.Equal(metric.UnavailableReason, projected.UnavailableReason);
        Assert.Null(projected.TotalValue); Assert.Empty(projected.Software);
    }

    internal static ResourceBreakdownSnapshot SharedOnlySnapshot()
    {
        var source = ResourceFactsPublicationTests.Snapshot();
        var process = new ResourceProcessSegment(42, "synthetic.exe", null, 3350528, 0, 100) { ProcessStartKey = long.MaxValue };
        var owner = new ResourceSoftwareSegment("example", "Example", "other", "other", 3350528, 0, 1, [process]);
        return source with
        {
            Bars = [new("gpu.1.vram", "B", "capacity", null, 0, null, [owner]) { SharedValue = 29360128 }],
            Datasets = [source.Datasets[1]]
        };
    }

    private static HardwareMetricSnapshot Hardware(bool? capability)
        => new(DateTimeOffset.UtcNow, HardwareMetricTestData.EmptyCpu, HardwareMetricTestData.EmptyMemory,
            HardwareMetricTestData.EmptyVirtualMemory,
            [new GpuMetrics(1, "GPU (name does not prove capability)", 0, 0, 0, 0, 0, 0, 0, 0,
                new(new("test", HardwareSensorProviderStates.Unavailable, null), null, null, null, null, null, null, null, null, null, null), IsUsageAvailable: false)
                { HasDedicatedMemoryMetrics = capability, AdapterKey = 23 }], HardwareMetricTestData.EmptyGpuInventory,
            new Dictionary<string, MetricReadingValue> { ["gpu.1.vram"] = new("gpu.1.vram", null, "B") });
}
