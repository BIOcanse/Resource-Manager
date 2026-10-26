using System.Text.Json;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Endpoints.Transport;

namespace Resource_Manager_APP.Tests;

public sealed class ResourceFactsPublicationTests
{
    [Theory]
    [InlineData("memory.usage", "process_all_private_working_set_actual")]
    [InlineData("virtualMemory.usage", "process_all_private_commit_actual")]
    public void AutomaticMemoryFactsNameTheirActualProcessField(string metricId, string field)
    {
        var snapshot = BackendFrontendWireAcceptanceTests.BuildSamplerBars([metricId], available: true);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ResourceFactsSnapshot.Create(snapshot, [metricId]), options));
        Assert.Equal(field, Assert.Single(json.RootElement.GetProperty("metrics").EnumerateArray()).GetProperty("processFieldName").GetString());
    }

    [Fact]
    public void IdentityAndAttributionFactsNeedNoHiddenNumericMetricAndExcludeExitedInstances()
    {
        var time = DateTimeOffset.UtcNow;
        var process = new SchedulingProcessFact(42, 100, "example.exe", "C:/example.exe", "example", "Example", "other", "other",
            0, SchedulingProcessMetricMask.None, 0, 0, 1, []) { UserName = "user", Architecture = "x64" };
        var foundations = new SchedulingProcessFactSnapshot(SamplingObservationStatus.Current, 1, time.UtcTicks, 1, 0, 0, 0,
            SchedulingProcessMetricMask.None, SchedulingProcessMetricMask.None, [])
        { HasSeparateFoundationPayloads = true, InventoryProcesses = [process], AttributionProcesses = [process] };
        var facts = ResourceFactsSnapshot.Create(new(time, []), [], foundations);
        Assert.Equal(2, facts.Version);
        Assert.Empty(facts.Metrics);
        Assert.Equal("Example", Assert.Single(facts.Software).Name);
        Assert.Equal("user", Assert.Single(facts.Processes).UserName);
        Assert.Equal("x64", Assert.Single(facts.Processes).Architecture);
        Assert.Equal("example", Assert.Single(facts.Attributions).SoftwareId);
        var expired = ResourceFactsSnapshot.Create(new(time, []), [], foundations with { InventoryProcesses = [] });
        Assert.Empty(expired.Attributions); Assert.Empty(expired.Processes); Assert.Empty(expired.Software);
    }

    [Fact]
    public void MissingSystemTotalDoesNotClearProcessValuesAndFailedDatasetClearsFacts()
    {
        const string field = "process_all_private_commit_actual";
        var source = Snapshot();
        source = source with
        {
            Bars = [],
            ProcessMemoryFacts = [new(field, "virtualMemory.usage", null, null, 12, [new(42, long.MaxValue, 12)])],
            Datasets = [source.Datasets[0] with { DatasetId = SamplingDatasetIds.ForProcessMetric(field) }]
        };
        var foundations = Foundations();
        var published = ResourceFactsSnapshot.Create(source, [field], foundations);
        var metric = Assert.Single(published.Metrics);
        Assert.Equal(Assert.Single(published.Attributions).ProcessIndex,
            Assert.Single(Assert.Single(metric.Software).Processes).ProcessIndex);
        Assert.Null(metric.TotalValue);
        Assert.Null(metric.CapacityValue);
        Assert.Equal(12, metric.ProcessSumValue);
        Assert.Equal(12, Assert.Single(Assert.Single(metric.Software).Processes).Value);
        Assert.Empty(ResourceFactsSnapshot.Create(source, []).Processes);
        source = source with { Datasets = [source.Datasets[0] with { Status = "failed" }] };
        var failed = ResourceFactsSnapshot.Create(source, [field], foundations);
        var failedMetric = Assert.Single(failed.Metrics);
        Assert.Equal(field, failedMetric.MetricId);
        Assert.Null(failedMetric.TotalValue);
        Assert.Empty(failedMetric.Software);
        Assert.Equal("resource-breakdown-total-unavailable", failedMetric.UnavailableReason);
        Assert.Single(failed.Processes);
        Assert.Single(failed.Attributions);
        Assert.Equal(new DateTimeOffset(foundations.ObservedAtUtcTicks, TimeSpan.Zero), failed.CapturedAt);
    }

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
        Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32());
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
        Assert.Equal(new[] { "cpu.usage", "gpu.1.vram" }, facts.Metrics.Select(metric => metric.MetricId));
        Assert.Null(facts.Metrics[0].TotalValue);
        Assert.NotNull(facts.Metrics[1].TotalValue);
        var empty = ResourceFactsSnapshot.Create(source, ["cpu.usage"]);
        var metric = Assert.Single(empty.Metrics);
        Assert.Null(metric.TotalValue);
        Assert.Equal("resource-breakdown-total-unavailable", metric.UnavailableReason);
        Assert.Empty(empty.Software);
        Assert.Empty(empty.Processes);
        Assert.Null(empty.CapturedAt);
    }

    [Fact]
    public void EveryRequestedFactRemainsPresentAcrossAvailabilityFlips()
    {
        string[] ids = ["disk.io", "network.traffic", "process_all_private_commit_actual"];
        var current = BackendFrontendWireAcceptanceTests.BuildSamplerBars(ids[..2], true, throughputMultiplier: 0);
        foreach (var available in new[] { true, false, true, false })
        {
            var source = available ? current : current with { Bars = [], Datasets = [] };
            var facts = ResourceFactsSnapshot.Create(source, ids);
            Assert.Equal(ids, facts.Metrics.Select(metric => metric.MetricId));
            Assert.All(facts.Metrics, metric =>
            {
                if (metric.TotalValue is null) Assert.False(string.IsNullOrWhiteSpace(metric.UnavailableReason));
                else Assert.Equal(0, metric.TotalValue);
            });
        }
    }

    internal static SchedulingProcessFactSnapshot Foundations(long startKey = long.MaxValue)
    {
        var time = DateTimeOffset.UnixEpoch;
        var process = new SchedulingProcessFact(42, (ulong)startKey, "example.exe", null, "example", "Example", "other", "other",
            5, SchedulingProcessMetricMask.None, 0, 0, 1, []) { UserName = "user", Architecture = "x64" };
        return new(SamplingObservationStatus.Current, 1, time.UtcTicks, 1, 0, 0, 0,
            SchedulingProcessMetricMask.None, SchedulingProcessMetricMask.None, [])
        { HasSeparateFoundationPayloads = true, InventoryProcesses = [process], AttributionProcesses = [process] };
    }

    [Fact]
    public void MemoryFieldsReferenceOneFoundationInstanceAndNeverAttachOldValuesToAReusedPid()
    {
        string[] fields = ["process_all_working_set_actual", "process_all_private_commit_actual"];
        var source = Snapshot() with
        {
            Bars = [],
            ProcessMemoryFacts = fields.Select(field => new ProcessMemoryFact(field, ProcessMemoryFactFields.TotalMetricId(field)!,
                20, 100, 12, [new(42, long.MaxValue, 12)])).ToArray(),
            Datasets = [Snapshot().Datasets[0] with { DatasetId = SamplingDatasetIds.ForProcessMetric(fields[0]) }]
        };
        var foundations = Foundations();
        var facts = ResourceFactsSnapshot.Create(source, fields, foundations);
        Assert.Single(facts.Processes, process => process.ProcessId > 0);
        var attribution = Assert.Single(facts.Attributions, edge => edge.SoftwareId == "example");
        Assert.All(facts.Metrics, metric => Assert.Equal(attribution.ProcessIndex,
            Assert.Single(Assert.Single(metric.Software, owner => owner.SoftwareId == "example").Processes).ProcessIndex));
        var reused = ResourceFactsSnapshot.Create(source, fields, foundations with
        { InventoryProcesses = Foundations(long.MaxValue - 1).InventoryProcesses });
        Assert.DoesNotContain(reused.Processes, process => process.ProcessId > 0);
        Assert.All(reused.Attributions, edge => Assert.StartsWith("resource-residual:", edge.SoftwareId));
        Assert.All(reused.Metrics, metric =>
        {
            Assert.Equal(0, metric.ProcessSumValue);
            Assert.Equal(20, metric.TotalValue);
            if (metric.AdditiveAcrossProcesses) Assert.Equal(20, metric.ResidualValue);
            else { Assert.Empty(metric.Software); Assert.Null(metric.ResidualValue); }
        });
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
