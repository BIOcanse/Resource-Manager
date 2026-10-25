using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;

namespace Resource_Manager_APP.Tests;

public sealed partial class BackendFrontendWireAcceptanceTests
{
    [Theory]
    [InlineData("process_all_working_set_actual", "memory.usage")]
    [InlineData("process_all_private_working_set_actual", "memory.usage")]
    [InlineData("process_all_private_commit_actual", "virtualMemory.usage")]
    public async Task ProcessMemoryFactsSerializerFeedsProductionDecoder(string field, string total)
    {
        var source = ResourceFactsPublicationTests.Snapshot();
        var snapshot = source with
        {
            Bars = [],
            ProcessMemoryFacts = [new(field, total, 20, 100, 12, [new(42, long.MaxValue, 12)])],
            Datasets = [source.Datasets[0] with { DatasetId = SamplingDatasetIds.ForProcessMetric(field) }]
        };
        var facts = ResourceFactsSnapshot.Create(snapshot, [field], ResourceFactsPublicationTests.Foundations());
        Assert.Equal(total, Assert.Single(facts.Metrics).TotalMetricId);
        Assert.Equal(12, facts.Metrics[0].ProcessSumValue);
        Assert.Equal(Assert.Single(facts.Attributions).ProcessIndex,
            Assert.Single(Assert.Single(facts.Metrics[0].Software).Processes).ProcessIndex);
        var result = await DecodeAsync("facts", facts);
        Assert.Equal(long.MaxValue.ToString(), result.GetProperty("factsProcessStartKey").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("factsProcessSharedValue").ValueKind);
    }
}
