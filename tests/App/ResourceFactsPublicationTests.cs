using System.Text.Json;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Endpoints.Transport;

namespace Resource_Manager_APP.Tests;

public sealed class ResourceFactsPublicationTests
{
    [Fact]
    public void EmptyFactsMatchProjectionOfAnEmptySelectionUsingProductionJsonContext()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);

        Assert.Equal(
            JsonSerializer.Serialize(ResourceFactsSnapshot.Create(Snapshot(), []), options),
            JsonSerializer.Serialize(ResourceFactsSnapshot.Empty, options));
    }

    [Fact]
    public void PublishesIdentityOnceAndReferencesItAcrossMetricValuesUsingProductionJsonContext()
    {
        var current = Snapshot();
        var facts = ResourceFactsSnapshot.Create(current, ["cpu.usage", "gpu.1.vram"]);
        Assert.Single(facts.Software);
        var process = Assert.Single(facts.Processes);
        Assert.Equal(long.MaxValue.ToString(), process.ProcessStartKey);
        Assert.Equal("user", process.UserName);
        Assert.All(facts.Metrics, metric => Assert.Equal(0, Assert.Single(Assert.Single(metric.Software).Processes).ProcessIndex));
        Assert.Equal(4, facts.Metrics[1].Software[0].Processes[0].SharedValue);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var json = JsonSerializer.Serialize(facts, options);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(1, parsed.RootElement.GetProperty("version").GetInt32());
        foreach (var name in new[] { "columns", "width", "visible", "sortable", "rows", "sort", "sortKeys", "viewMode", "displayValue", "label", "scaleMode", "failureCode", "datasets" })
            Assert.DoesNotContain($"\"{name}\"", json);
        Assert.Equal(1, json.Split("example.exe").Length - 1);
    }

    [Fact]
    public void MissingOrFailedFactClearsOnlyThatMetricAndNeverPublishesStaleIdentities()
    {
        var source = Snapshot();
        source = source with { Datasets = source.Datasets.Select((state, index) => index == 0 ? state with { Status = "failed" } : state).ToArray() };
        var facts = ResourceFactsSnapshot.Create(source, ["cpu.usage", "gpu.1.vram"]);
        Assert.Equal("gpu.1.vram", Assert.Single(facts.Metrics).MetricId);
        var empty = ResourceFactsSnapshot.Create(source, ["cpu.usage"]);
        Assert.Empty(empty.Metrics);
        Assert.Empty(empty.Software);
        Assert.Empty(empty.Processes);
        Assert.Null(empty.CapturedAt);
    }

    internal static ResourceBreakdownSnapshot Snapshot()
    {
        var time = DateTimeOffset.UnixEpoch;
        var process = new ResourceProcessSegment(42, "example.exe", null, 12, 12, 100, "user", "x64")
            { ProcessStartKey = long.MaxValue, SharedValue = 4 };
        var software = new ResourceSoftwareSegment("example", "Example", "other", "other", 12, 12, 1, [process]) { SharedValue = 4 };
        var bars = new[] { "cpu.usage", "gpu.1.vram" }.Select(id => new ResourceBreakdownBar(id, "%", "capacity", 12, 100, 12, [software]) { SharedValue = 4 }).ToArray();
        return new ResourceBreakdownSnapshot(time, bars) { Datasets = bars.Select(bar =>
            new ResourceBreakdownDatasetSamplingState(SamplingDatasetIds.ForProcessMetric(bar.MetricId), "ready", 1, 1, time, time, time, null, null, null)).ToArray() };
    }
}
