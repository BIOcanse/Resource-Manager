using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class ResourceMeasureFactsTests
{
    [Theory]
    [InlineData("memory.usage")]
    [InlineData("MEMORY.USAGE")]
    public void AutomaticCounterPartsKeepCaseInsensitiveMetricIdentity(string id)
    {
        var source = FieldSnapshot("process_all_private_working_set_actual", 1000, 60);
        var provider = PhysicalMemoryResidualBreakdownProviderTests.Provider(PhysicalMemoryResidualBreakdownProviderTests.Fixture());
        var captured = provider.CreateResidualSegments(PhysicalMemoryResidualBreakdownProviderTests.Request());
        var residual = new ResourceSoftwareSegment("resource-residual:" + id, string.Empty,
            "windows_system", "system-residual", 1000, 50, 0, captured, 0);
        source = source with
        {
            ProcessMemoryFacts = [],
            Bars = [new(id, "B", "capacity", 1000, 2000, 50, [residual]) { ResidualSource = "counters" }],
            Datasets = [source.Datasets[0] with { DatasetId = SamplingDatasetIds.ForProcessMetric(id) }]
        };
        var bar = Assert.Single(ResourceBreakdownWireSnapshot.Create(source, [id]).Bars);
        Assert.Equal("counters", bar.ResidualSource);
        var rows = Assert.Single(bar.Software).Processes;
        Assert.Equal(captured.Select(row => row.AttributionKind), rows.Select(row => row.AttributionKind));
        Assert.Equal(captured.Select(row => row.Value), rows.Select(row => row.Value));
        Assert.Equal(1000, bar.ResidualValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhysicalCounterPartsSurvivePublishedProcessReconciliation(bool exited)
    {
        const string field = "process_all_private_working_set_actual";
        var source = FieldSnapshot(field, 1000, 60);
        var provider = PhysicalMemoryResidualBreakdownProviderTests.Provider(PhysicalMemoryResidualBreakdownProviderTests.Fixture());
        var captured = provider.CreateResidualSegments(PhysicalMemoryResidualBreakdownProviderTests.Request(940));
        var residual = new ResourceSoftwareSegment("resource-residual:memory.usage", string.Empty,
            "windows_system", "system-residual", 940, 47, 0, captured, 0);
        source = source with { ProcessMemoryFacts = [source.ProcessMemoryFacts[0] with { Residuals = [residual] }] };
        var foundations = ResourceFactsPublicationTests.Foundations();
        if (exited) foundations = foundations with { InventoryProcesses = [] };
        var facts = ResourceFactsSnapshot.Create(source, [field], foundations);
        var metric = Assert.Single(facts.Metrics);
        Assert.Equal("counters", metric.ResidualSource);
        Assert.Equal(exited ? 0 : 60, metric.ProcessSumValue);
        Assert.Equal(metric.TotalValue, metric.ProcessSumValue + metric.ResidualValue);
        var published = Assert.Single(metric.Software, owner => owner.SoftwareId == residual.SoftwareId);
        Assert.Equal(metric.ResidualValue, published.Processes.Sum(row => row.Value));
        foreach (var part in captured.Where(row => row.AttributionKind != PhysicalMemoryResidualKinds.Unclassified))
            Assert.Equal(part.Value, Assert.Single(published.Processes,
                row => facts.Processes[row.ProcessIndex].AttributionKind == part.AttributionKind).Value);
        Assert.Equal(exited ? 650 : 590, Assert.Single(published.Processes,
            row => facts.Processes[row.ProcessIndex].AttributionKind == PhysicalMemoryResidualKinds.Unclassified).Value);
        var bar = Assert.Single(ResourceBreakdownWireSnapshot.Create(source, [field], foundations: foundations).Bars);
        Assert.Equal("counters", bar.ResidualSource);
        Assert.Equal(published.Processes.Select(row => row.Value),
            Assert.Single(bar.Software, owner => owner.SoftwareId == residual.SoftwareId).Processes.Select(row => row.Value));
    }

    [Theory]
    [InlineData("process_all_cpu_usage_actual")]
    [InlineData("process_all_private_commit_actual")]
    [InlineData("process_all_working_set_actual")]
    [InlineData("process_all_network_receive_bytes_attributed")]
    public void OtherMeasuresNeverClaimPhysicalMemoryCounters(string field)
    {
        var source = FieldSnapshot(field, 100, 60);
        var foundations = ResourceFactsPublicationTests.Foundations();
        Assert.Null(Assert.Single(ResourceFactsSnapshot.Create(source, [field], foundations).Metrics).ResidualSource);
        Assert.Null(Assert.Single(ResourceBreakdownWireSnapshot.Create(source, [field], foundations: foundations).Bars).ResidualSource);
    }

    [Theory]
    [InlineData("process_all_cpu_usage_actual", "cpu.usage", true)]
    [InlineData("process_all_working_set_actual", "memory.usage", false)]
    [InlineData("process_all_private_working_set_actual", "memory.usage", true)]
    [InlineData("process_all_private_commit_actual", "virtualMemory.usage", true)]
    [InlineData("process_all_network_receive_bytes_attributed", "network.traffic", true)]
    [InlineData("process_all_network_send_bytes_attributed", "network.traffic", true)]
    [InlineData("process_all_network_receive_rate_attributed", "network.traffic", true)]
    [InlineData("process_all_network_send_rate_attributed", "network.traffic", true)]
    [InlineData("process_all_physical_disk_read_bytes_attributed", "disk.io", true)]
    [InlineData("process_all_physical_disk_write_bytes_attributed", "disk.io", true)]
    public void CatalogDeclaresExactMeasureAndAdditivity(string name, string total, bool additive)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ProcessMetricFieldRegistry.Find(name), options));
        Assert.True(json.RootElement.GetProperty("supportsResourceFacts").GetBoolean());
        Assert.Equal(total, json.RootElement.GetProperty("resourceFactTotalMetricId").GetString());
        Assert.Equal(additive, json.RootElement.GetProperty("additiveAcrossProcesses").GetBoolean());
        Assert.False(ProcessMetricFieldRegistry.Find("process_all_peak_private_commit_actual")!.SupportsResourceFacts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OnePublishedFrameBalancesAfterExitOrPidReuse(bool exited, bool reused)
    {
        const string name = "process_all_private_working_set_actual";
        var source = FieldSnapshot(name, 100, 60);
        var foundations = ResourceFactsPublicationTests.Foundations();
        if (exited) foundations = foundations with { InventoryProcesses = [] };
        if (reused) foundations = ResourceFactsPublicationTests.Foundations(long.MaxValue - 1);
        var result = ResourceFactsSnapshot.Create(source, [name], foundations);
        var metric = Assert.Single(result.Metrics);
        Assert.Equal(exited || reused ? 0 : 60, metric.ProcessSumValue);
        Assert.Equal(metric.TotalValue, metric.ProcessSumValue + metric.ResidualValue);
        var real = metric.Software.SelectMany(owner => owner.Processes)
            .Where(row => result.Processes[row.ProcessIndex].ProcessId > 0).Sum(row => row.Value);
        Assert.Equal(real, metric.ProcessSumValue);
        Assert.Equal(metric.ResidualValue, Assert.Single(metric.Software, owner => owner.SoftwareId.StartsWith("resource-residual:")).Value);
        Assert.Equal(source.CapturedAt, metric.CapturedAt);
        var bar = Assert.Single(ResourceBreakdownWireSnapshot.Create(source, [name], foundations: foundations).Bars);
        Assert.Equal(metric.ProcessSumValue, bar.ProcessSumValue);
        Assert.Equal(metric.ResidualValue, bar.ResidualValue);
    }

    [Fact]
    public void NonAdditiveWorkingSetHasNoResidualEvenWhenBelowMachineTotal()
    {
        const string name = "process_all_working_set_actual";
        var metric = Assert.Single(ResourceFactsSnapshot.Create(FieldSnapshot(name, 100, 60), [name],
            ResourceFactsPublicationTests.Foundations()).Metrics);
        Assert.False(metric.AdditiveAcrossProcesses);
        Assert.Null(metric.ResidualValue);
        Assert.Single(metric.Software);
        Assert.Equal(60, metric.ProcessSumValue);
    }

    [Theory]
    [InlineData("process_all_private_commit_actual")]
    [InlineData("process_all_private_working_set_actual")]
    public void AdditiveOverrunIsExplicitAndNeverScalesProcessValues(string field)
    {
        var metric = Assert.Single(ResourceFactsSnapshot.Create(FieldSnapshot(field, 10, 60), [field],
            ResourceFactsPublicationTests.Foundations()).Metrics);
        Assert.Equal(60, metric.ProcessSumValue);
        Assert.Equal(-50, metric.ResidualValue);
        Assert.Equal(10, metric.TotalValue);
        Assert.Equal(metric.TotalValue, metric.ProcessSumValue + metric.ResidualValue);
    }

    [Fact]
    public void AutomaticFactBalancesThePublishedProcessValuesAndTinyResidual()
    {
        var source = ResourceFactsPublicationTests.Snapshot();
        var bar = source.Bars[0] with { TotalValue = 12.25 };
        var result = ResourceFactsSnapshot.Create(source with { Bars = [bar] }, [bar.MetricId]);
        var metric = Assert.Single(result.Metrics);
        Assert.Equal(12, metric.ProcessSumValue);
        Assert.Equal(.25, metric.ResidualValue);
        Assert.Equal(metric.TotalValue, metric.ProcessSumValue + metric.ResidualValue);
    }

    [Theory]
    [InlineData("process_all_network_receive_bytes_attributed", 50, 20, "B")]
    [InlineData("process_all_network_send_bytes_attributed", 90, 30, "B")]
    [InlineData("process_all_network_receive_rate_attributed", 25, 10, "B/s")]
    [InlineData("process_all_network_send_rate_attributed", 45, 15, "B/s")]
    [InlineData("process_all_physical_disk_read_bytes_attributed", 50, 20, "B")]
    [InlineData("process_all_physical_disk_write_bytes_attributed", 90, 30, "B")]
    public void DirectionalTotalComesFromSameWindowIncludingUnattributedEvents(string name, double total, double process, string unit)
    {
        var time = DateTimeOffset.UnixEpoch.AddSeconds(5);
        var field = ProcessMetricFieldRegistry.Find(name)!;
        var fields = new ProcessMetricFieldSnapshot(time, 2000, [field],
            [new(42, long.MaxValue.ToString(), [new(name, null, process)])])
        { Sources = [new(field.SourceId, time, 2000)] };
        var network = new ProcessNetworkTrafficWindow(time, 2000,
            [new(42, long.MaxValue.ToString(), "20", "30", null, null), new(99, null, "30", "60", null, null)]);
        var disk = new PhysicalDiskProcessIoWindow(time, 2000,
            [new(new(1, 0), 42, long.MaxValue.ToString(), "20", "30"), new(new(1, 0), 99, null, "30", "60")]);
        var fact = Assert.Single(WindowsResourceBreakdownSampler.CreateProcessMeasureFacts([field], fields, null, network, disk));
        Assert.Equal(total, fact.TotalValue);
        Assert.Equal(process, fact.ProcessSumValue);
        Assert.Equal(time, fact.CapturedAt);
        Assert.Equal(2000, fact.WindowMilliseconds);
        Assert.Equal(unit, fact.Unit);
        var source = FieldSnapshot(name, total, process) with { ProcessMemoryFacts = [fact] };
        var metric = Assert.Single(ResourceFactsSnapshot.Create(source, [name], ResourceFactsPublicationTests.Foundations()).Metrics);
        Assert.Equal(total, metric.ProcessSumValue + metric.ResidualValue);
        Assert.Equal(total - process, metric.ResidualValue);
        Assert.Equal(time, metric.CapturedAt);
    }

    [Fact]
    public void AutomaticFactsRecomputeOwnerSharesAndSharedOwnershipAfterExit()
    {
        var source = ResourceFactsPublicationTests.Snapshot();
        var original = source.Bars[1];
        var survivor = original.Software[0].Processes[0] with
            { Value = 40, SoftwarePercent = 40, SharedValue = 5 };
        var exited = survivor with { ProcessId = 43, Value = 60, SoftwarePercent = 60, SharedValue = 10 };
        var owner = original.Software[0] with
            { Value = 100, ProcessCount = 2, SharedValue = 15, Processes = [survivor, exited] };
        source = source with { Bars = [original with
            { TotalValue = 100, CapacityValue = 200, TotalSystemPercent = 50, SharedValue = 15, Software = [owner] }] };
        var foundations = ResourceFactsPublicationTests.Foundations();
        var metric = Assert.Single(ResourceFactsSnapshot.Create(source, ["gpu.1.vram"], foundations).Metrics);
        var remaining = Assert.Single(metric.Software, value => value.SoftwareId == owner.SoftwareId);
        Assert.Equal(40, remaining.Value);
        Assert.Equal(100, Assert.Single(remaining.Processes).SoftwarePercent);
        Assert.Equal(5, remaining.SharedValue);
        Assert.Equal(5, metric.SharedValue);
        Assert.Equal(40, metric.ProcessSumValue);
        Assert.Equal(60, metric.ResidualValue);
        Assert.Equal(metric.TotalValue, metric.ProcessSumValue + metric.ResidualValue);
        var bar = Assert.Single(ResourceBreakdownWireSnapshot.ProjectResourceBars(source, ["gpu.1.vram"], foundations));
        Assert.Equal(5, bar.SharedValue);
        Assert.Equal(100, Assert.Single(Assert.Single(bar.Software,
            value => value.SoftwareId == owner.SoftwareId).Processes).SoftwarePercent);
    }
    [Theory]
    [InlineData("gpu.0.vram")]
    [InlineData("gpu.3.vram")]
    [InlineData("gpu.12.vram")]
    public void DetailsKeepTheCapturedBalanceWhenRequestedOwnersHaveNoProcessAllocations(string id)
    {
        var snapshot = BackendFrontendWireAcceptanceTests.BuildSamplerBars([id], available: true);
        var originalOwner = Assert.Single(Assert.Single(snapshot.Bars).Software);
        Assert.Empty(originalOwner.Processes);
        var facts = Assert.Single(ResourceFactsSnapshot.Create(snapshot, [id]).Metrics);
        Assert.Equal(0, facts.ProcessSumValue);
        Assert.Equal(facts.TotalValue, facts.ResidualValue);
        var details = ResourceProcessDetailsWireSnapshot.Create(snapshot, [id],
            new HashSet<string>([originalOwner.SoftwareId]));
        var metric = Assert.Single(details.Metrics);
        Assert.Equal(id, metric.MetricId);
        Assert.Empty(metric.Software);
        Assert.Equal(facts.TotalValue, metric.TotalValue);
        Assert.Equal(facts.ProcessSumValue, metric.ProcessSumValue);
        Assert.Equal(facts.ResidualValue, metric.ResidualValue);
        Assert.Equal(metric.TotalValue, metric.ProcessSumValue + metric.ResidualValue);
        Assert.Equal(facts.CapturedAt, metric.CapturedAt);
        Assert.Equal(metric.CapturedAt, details.CapturedAt);
    }
    internal static ResourceBreakdownSnapshot FieldSnapshot(string field, double total, double value)
    {
        var source = ResourceFactsPublicationTests.Snapshot();
        return source with
        {
            Bars = [],
            ProcessMemoryFacts = [new(field, ProcessMemoryFactFields.TotalMetricId(field)!, total, 200, value, [new(42, long.MaxValue, value)])
            { Unit = ProcessMetricFieldRegistry.Find(field)!.Unit, CapturedAt = source.CapturedAt }],
            Datasets = [source.Datasets[0] with { DatasetId = SamplingDatasetIds.ForProcessMetric(field) }]
        };
    }
}
