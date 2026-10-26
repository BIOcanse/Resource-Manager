using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;

namespace Resource_Manager_APP.Tests;

public sealed partial class BackendFrontendWireAcceptanceTests
{
    [Fact]
    public async Task SharedOnlyIntegratedGpuFactsArePresentAndUnavailableInProductionDecoder()
    {
        var facts = ResourceFactsSnapshot.Create(GpuMemoryResourceFactTests.SharedOnlySnapshot(), ["gpu.1.vram"]);
        var metric = Assert.Single(facts.Metrics);
        Assert.Null(metric.TotalValue); Assert.Null(metric.ProcessSumValue); Assert.Null(metric.ResidualValue);
        Assert.NotNull(metric.UnavailableReason);
        var result = await DecodeAsync("facts", facts);
        Assert.Equal("gpu.1.vram", Assert.Single(result.GetProperty("decodedMetricIds").EnumerateArray()).GetString());
        Assert.Equal(0, result.GetProperty("processCount").GetInt32());
    }
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
        Assert.Contains(facts.Attributions, edge => edge.SoftwareId == "example" && edge.ProcessIndex ==
            Assert.Single(Assert.Single(facts.Metrics[0].Software, owner => owner.SoftwareId == "example").Processes).ProcessIndex);
        var result = await DecodeAsync("facts", facts);
        Assert.Equal(long.MaxValue.ToString(), result.GetProperty("factsProcessStartKey").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("factsProcessSharedValue").ValueKind);
        var bars = ResourceBreakdownWireSnapshot.Create(snapshot, [field],
            new HashSet<string>(["example"]), ResourceFactsPublicationTests.Foundations());
        Assert.Single(bars.Bars);
        var decodedBar = await DecodeAsync("monitor", new ResourceMonitorWireSnapshot(
            ResourceMonitorWireSnapshot.CurrentVersion, bars.CapturedAt, bars));
        Assert.Equal(long.MaxValue.ToString(), decodedBar.GetProperty("breakdownProcessStartKey").GetString());
    }

    [Theory]
    [InlineData("process_all_cpu_usage_actual")]
    [InlineData("process_all_network_receive_rate_attributed")]
    [InlineData("process_all_physical_disk_write_bytes_attributed")]
    [InlineData("process_all_private_commit_actual")]
    [InlineData("process_all_gpu_residency_attributed_gpu_0_luid_0000000000000017_physical_0_engine_1")]
    [InlineData("process_all_gpu_resident_bytes_attributed_gpu_0_luid_0000000000000017")]
    public async Task AllResourceMeasureFactsFeedProductionDecodersWithFrameBalance(string name)
    {
        var source = ResourceMeasureFactsTests.FieldSnapshot(name, 100, 60);
        var facts = ResourceFactsSnapshot.Create(source, [name], ResourceFactsPublicationTests.Foundations());
        var metric = Assert.Single(facts.Metrics);
        Assert.True(metric.AdditiveAcrossProcesses);
        Assert.Equal(metric.TotalValue, metric.ProcessSumValue + metric.ResidualValue);
        var decoded = await DecodeAsync("facts", facts);
        Assert.Equal(long.MaxValue.ToString(), decoded.GetProperty("factsProcessStartKey").GetString());
        var bars = ResourceBreakdownWireSnapshot.Create(source, [name], new HashSet<string>(["example"]),
            ResourceFactsPublicationTests.Foundations());
        var decodedBar = await DecodeAsync("monitor", new ResourceMonitorWireSnapshot(
            ResourceMonitorWireSnapshot.CurrentVersion, bars.CapturedAt, bars));
        Assert.Equal(long.MaxValue.ToString(), decodedBar.GetProperty("breakdownProcessStartKey").GetString());
    }
}
