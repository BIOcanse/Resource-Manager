using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed partial class BackendFrontendWireAcceptanceTests
{
    [Theory]
    [InlineData("disk.io", "running")]
    [InlineData("disk.io", "warming")]
    [InlineData("disk.io", "unavailable")]
    [InlineData("network.traffic", "running")]
    [InlineData("network.traffic", "warming")]
    [InlineData("network.traffic", "unavailable")]
    public async Task ZeroAndNotCurrentThroughputStayPresentInBarsAndFacts(string id, string state)
    {
        var readable = state == "running";
        var snapshot = BuildSamplerBars([id], true, throughputMultiplier: 0, throughputState: state);
        var sourceBar = Assert.Single(snapshot.Bars);
        Assert.Equal(readable ? SamplingObservationStatus.Current : state == "warming"
                ? SamplingObservationStatus.Warming : SamplingObservationStatus.Unavailable,
            sourceBar.ObservationStatus);
        Assert.Equal("active", sourceBar.ScaleMode);
        var wire = ResourceBreakdownWireSnapshot.Create(snapshot, [id],
            scaleModes: new Dictionary<string, string> { [id] = "active" });
        Assert.Equal("active", Assert.Single(wire.Bars).ScaleMode);
        object[] payloads = [new ResourceMonitorWireSnapshot(ResourceMonitorWireSnapshot.CurrentVersion, wire.CapturedAt, wire),
            ResourceFactsSnapshot.Create(snapshot, [id])];
        string[] kinds = ["monitor", "facts"];
        for (var index = 0; index < kinds.Length; index++)
        {
            var decoded = await DecodeAsync(kinds[index], payloads[index], [id]);
            AssertDecodedMetrics(decoded, [id], 1);
            var value = Assert.Single(decoded.GetProperty("decodedValues").EnumerateArray());
            Assert.Equal(0, value.GetProperty("segments").GetInt32());
            if (readable)
            {
                Assert.Equal(0, value.GetProperty("total").GetDouble());
                Assert.Equal(1, value.GetProperty("capacity").GetDouble());
                Assert.Equal(0, value.GetProperty("percent").GetDouble());
                Assert.Equal(System.Text.Json.JsonValueKind.Null, value.GetProperty("reason").ValueKind);
            }
            else
            {
                Assert.Equal(System.Text.Json.JsonValueKind.Null, value.GetProperty("total").ValueKind);
                Assert.Equal(System.Text.Json.JsonValueKind.Null, value.GetProperty("capacity").ValueKind);
                Assert.Equal(System.Text.Json.JsonValueKind.Null, value.GetProperty("percent").ValueKind);
                Assert.Equal(sourceBar.UnavailableReason, value.GetProperty("reason").GetString());
                Assert.False(string.IsNullOrWhiteSpace(sourceBar.UnavailableReason));
            }
        }
    }

    [Theory]
    [InlineData("warming")]
    [InlineData("failed")]
    [InlineData("missing")]
    public async Task UnreadableDatasetsKeepRequestedItemsInBothProductionDecoders(string status)
    {
        string[] ids = ["disk.io", "network.traffic", "virtualMemory.usage"];
        var snapshot = BuildSamplerBars(ids, true);
        snapshot = snapshot with { Bars = [], Datasets = status == "missing" ? []
            : snapshot.Datasets.Select(dataset => dataset with { Status = status,
                CapturedAt = null, FailureCode = status == "failed" ? "resource-breakdown-dataset-sample-failed" : null }).ToArray() };
        var wire = ResourceBreakdownWireSnapshot.Create(snapshot, ids);
        object[] payloads = [new ResourceMonitorWireSnapshot(ResourceMonitorWireSnapshot.CurrentVersion, wire.CapturedAt, wire),
            ResourceFactsSnapshot.Create(snapshot, ids)];
        string[] kinds = ["monitor", "facts"];
        for (var index = 0; index < kinds.Length; index++)
        {
            var decoded = await DecodeAsync(kinds[index], payloads[index], ids);
            AssertDecodedMetrics(decoded, ids, ids.Length);
            Assert.All(decoded.GetProperty("decodedValues").EnumerateArray(), value =>
            {
                Assert.Equal(System.Text.Json.JsonValueKind.Null, value.GetProperty("total").ValueKind);
                Assert.Equal(0, value.GetProperty("segments").GetInt32());
                Assert.Equal(status == "warming" ? "resource-breakdown-warming"
                    : status == "failed" ? "resource-breakdown-dataset-sample-failed"
                    : "resource-breakdown-total-unavailable", value.GetProperty("reason").GetString());
            });
        }
    }

    [Fact]
    public void MissingGpuSegmentsKeepKnownTotalAndPublishTheirReason()
    {
        var bar = Assert.Single(BuildSamplerBars(["gpu.0.usage"], available: true).Bars);
        Assert.NotNull(bar.TotalValue);
        Assert.Empty(bar.Software);
        Assert.Equal("resource-breakdown-attribution-unavailable", bar.UnavailableReason);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResidualProvidersFeedProductionDecodersAndLocalizedNames(bool etwEvidence)
    {
        var metricIds = etwEvidence ? new[] { "gpu.12.vram" }
            : new[] { "cpu.usage", "memory.usage", "virtualMemory.usage", "gpu.12.vram" };
        var scores = new CompiledBaseScorePlan(new Dictionary<string, double>(), new Dictionary<string, double>());
        var attribution = new WindowsResourceBreakdownSampler.ProcessAttributionSnapshot(
            [], new Dictionary<int, WindowsResourceBreakdownSampler.ProcessResourceSample>(),
            new Dictionary<int, ResourceManager.App.Domain.ProcessAttribution.RuntimeSoftwareAttribution>(), scores, []);
        // The controller owns the unstarted source. Evidence and process identity are fixture facts;
        // the real provider filters, groups, scales and constructs ETW and system-residual rows.
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        harness.CurrentProcessBirth = static pid => pid == 4747 ? 123456UL : null;
        var source = harness.Zone;
        var reads = 0;
        var etw = new DxgkrnlVidMmEtwResidualBreakdownProvider(source, window =>
        {
            reads++;
            Assert.Equal(TimeSpan.FromSeconds(45), window);
            return [new(4747, "evidence-worker", 2 << 20, "fixture", DateTimeOffset.UtcNow) { ProcessStartKey = 123456 }];
        });
        IResourceResidualBreakdownProvider provider = etwEvidence ? etw : new StaticResourceResidualBreakdownProvider();
        var bars = metricIds.Select(id => WindowsResourceBreakdownSampler.CreateBar(
            id, "B", ResourceBreakdownScaleModes.Capacity, 8 << 20, 4 << 20, [],
            attribution, scores, provider, isBytes: true)).ToArray();
        var snapshot = new ResourceBreakdownSnapshot(CapturedAt, bars)
        {
            Datasets = metricIds.Select(SamplingDatasetIds.ForProcessMetric).Distinct().Select(id =>
                new ResourceBreakdownDatasetSamplingState(id, ResourceBreakdownSamplingStatuses.Ready,
                    1, 1, CapturedAt, CapturedAt, CapturedAt, CapturedAt.AddSeconds(5), null, null)).ToArray()
        };
        Assert.Equal(etwEvidence ? 1 : 0, reads);
        Assert.Empty(harness.Sessions.Snapshot());
        var processes = bars.SelectMany(bar => bar.Software).SelectMany(software => software.Processes).ToArray();
        Assert.All(processes, process => Assert.Equal(
            process.AttributionKind == ResourceProcessAttributionKinds.SystemResidual ? "" : "evidence-worker", process.Name));
        Assert.Equal(etwEvidence ? 2 : 4, processes.Length);
        var softwareIds = bars.SelectMany(bar => bar.Software).Select(software => software.SoftwareId).ToHashSet();
        var wire = ResourceBreakdownWireSnapshot.Create(snapshot, metricIds, softwareIds);
        object[] payloads = [new ResourceMonitorWireSnapshot(ResourceMonitorWireSnapshot.CurrentVersion, wire.CapturedAt, wire),
            ResourceFactsSnapshot.Create(snapshot, metricIds), ResourceProcessDetailsWireSnapshot.Create(snapshot, metricIds, softwareIds)];
        string[] kinds = ["monitor", "facts", "details"];
        for (var index = 0; index < kinds.Length; index++)
        {
            var decoded = await DecodeAsync(kinds[index], payloads[index], metricIds);
            AssertDecodedMetrics(decoded, metricIds, metricIds.Length);
            var rows = decoded.GetProperty("decodedProcesses").EnumerateArray().ToArray();
            Assert.Equal(processes.Select(process => process.Name), rows.Select(row => row.GetProperty("name").GetString()));
            Assert.Equal(processes.Select(process => process.AttributionKind), rows.Select(row => row.GetProperty("attributionKind").GetString()));
            foreach (var language in decoded.GetProperty("localizedProcessNames").EnumerateObject())
            {
                Assert.Equal(processes.Length, language.Value.GetArrayLength());
                Assert.All(language.Value.EnumerateArray(), value => Assert.False(string.IsNullOrWhiteSpace(value.GetString())));
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SamplerBarsFeedProductionDecodersAndLocalizedNames(bool available)
    {
        var metricIds = typeof(ResourceBreakdownMetricIds).GetFields()
            .Where(field => field.IsLiteral && field.Name != nameof(ResourceBreakdownMetricIds.GpuPrefix))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Concat(new[] { 0, 3, 12 }.SelectMany(index => new[] { $"gpu.{index}.usage", $"gpu.{index}.vram" }))
            .ToArray();
        var snapshot = BuildSamplerBars(metricIds, available);
        Assert.Equal(metricIds, snapshot.Bars.Select(bar => bar.MetricId));
        Assert.All(snapshot.Bars, bar => Assert.Equal(available, bar.TotalValue.HasValue));
        if (!available) Assert.All(snapshot.Bars, bar => Assert.False(string.IsNullOrEmpty(bar.UnavailableReason)));
        Assert.All(snapshot.Bars.SelectMany(bar => bar.Software)
            .Where(software => software.SoftwareId == RuntimeAttributionIds.ResourceManagerSelf),
            software => Assert.Empty(software.Name));
        var softwareIds = snapshot.Bars.SelectMany(bar => bar.Software)
            .Select(software => software.SoftwareId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // These are the same three payload factories used by CompileResourceSubscription
        // and CompileResourceFactsSubscription. Empty bars retain their identity and reason.
        var wire = ResourceBreakdownWireSnapshot.Create(snapshot, metricIds, softwareIds);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(wire, WebJson));
        Assert.All(json.RootElement.GetProperty("bars").EnumerateArray(),
            bar => Assert.False(bar.TryGetProperty("label", out _)));
        var monitor = await DecodeAsync("monitor", new ResourceMonitorWireSnapshot(
            ResourceMonitorWireSnapshot.CurrentVersion, wire.CapturedAt, wire), metricIds);
        AssertDecodedMetrics(monitor, metricIds, metricIds.Length);
        if (!available)
        {
            var emptyBars = monitor.GetProperty("decodedEmptyBars").EnumerateArray().ToArray();
            Assert.Equal(metricIds.Length, emptyBars.Length);
            Assert.All(emptyBars, bar =>
            {
                Assert.False(string.IsNullOrWhiteSpace(bar.GetProperty("reason").GetString()));
                Assert.Equal(System.Text.Json.JsonValueKind.Null, bar.GetProperty("total").ValueKind);
                Assert.Equal(System.Text.Json.JsonValueKind.Null, bar.GetProperty("capacity").ValueKind);
                Assert.Equal(System.Text.Json.JsonValueKind.Null, bar.GetProperty("percent").ValueKind);
                Assert.Equal(0, bar.GetProperty("segments").GetInt32());
            });
        }

        // Facts retain metrics with null totals. The production facts decoder must accept them.
        var facts = await DecodeAsync("facts", ResourceFactsSnapshot.Create(snapshot, metricIds), metricIds);
        AssertDecodedMetrics(facts, metricIds, metricIds.Length);
        var detailsWire = ResourceProcessDetailsWireSnapshot.Create(snapshot, metricIds, softwareIds);
        Assert.Equal(available ? metricIds : [], detailsWire.Metrics.Select(metric => metric.MetricId));
        var details = await DecodeAsync("details", detailsWire, metricIds);
        AssertDecodedMetrics(details, available ? metricIds : [], metricIds.Length);
        if (available)
        {
            Assert.True(details.GetProperty("processCount").GetInt32() > 0);
            Assert.True(facts.GetProperty("processCount").GetInt32() > 0);
        }
    }

    private static void AssertDecodedMetrics(System.Text.Json.JsonElement result, string[] expected, int nameCount)
    {
        Assert.Equal(expected.Length, result.GetProperty("decodedMetricIds").GetArrayLength());
        Assert.Equal(expected, result.GetProperty("decodedMetricIds").EnumerateArray().Select(value => value.GetString()));
        var languages = result.GetProperty("localizedNames");
        Assert.True(languages.EnumerateObject().Count() > 2);
        foreach (var language in languages.EnumerateObject())
        {
            Assert.Equal(nameCount, language.Value.GetArrayLength());
            Assert.All(language.Value.EnumerateArray(), value => Assert.False(string.IsNullOrWhiteSpace(value.GetString())));
        }
    }

    internal static ResourceBreakdownSnapshot BuildSamplerBars(string[] metricIds, bool available,
        double throughputMultiplier = 1, string? throughputState = null)
    {
        var scores = new CompiledBaseScorePlan(new Dictionary<string, double>(), new Dictionary<string, double>());
        var process = new WindowsResourceBreakdownSampler.ProcessResourceSample(
            4242, null, 123456, "example.exe", @"C:\Apps\example.exe", 12, 12, TimeSpan.Zero,
            true, true, true, 12, true, false, null, null, null, null, null, null, null, null, 12, true);
        var attribution = new WindowsResourceBreakdownSampler.ProcessAttributionSnapshot(
            [process], new Dictionary<int, WindowsResourceBreakdownSampler.ProcessResourceSample> { [4242] = process },
            new Dictionary<int, RuntimeSoftwareAttribution>
            {
                [4242] = new(RuntimeAttributionIds.ResourceManagerSelf, ResourceManagerSelfDescriptor.DisplayName,
                    SoftwareKinds.Adapted, SoftwareDisplayKinds.Adapted, [])
            }, scores, []);
        var state = new HardwareSensorProviderState("fixture", HardwareSensorProviderStates.Unavailable, null);
        var hardware = new HardwareMetricSnapshot(CapturedAt,
            new CpuMetrics("CPU", 25, available, CpuMetricObservationStatus.Complete, 1, 1, 3000, 5000, 60,
                "fixture", new CpuSensorMetrics(state, null, null, null, null)),
            new MemoryMetrics(UsedBytes: 50, TotalBytes: 100, UsagePercent: 50, IsUsageAvailable: available),
            new VirtualMemoryMetrics(UsedBytes: 50, TotalBytes: 100, UsagePercent: 50, IsSelectable: available),
            new[] { 0, 3, 12 }.Select(index => new GpuMetrics(index, "fixture", 25, 1, 1, 100, 1, 50, 100, 50,
                new GpuSensorMetrics(state, null, null, null, null, null, null, null, null, null, null),
                IsUsageAvailable: available)).ToArray(),
            new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, []),
            metricIds.ToDictionary(id => id, id => new MetricReadingValue(Id: id, NumericValue: 50, Unit: "B")))
        {
            Datasets = metricIds.Select(SamplingDatasetIds.ForSystemMetric).Distinct().ToDictionary(id => id,
                id => new HardwareMetricDatasetObservation(id,
                    available ? SamplingObservationStatus.Current : SamplingObservationStatus.Unavailable,
                    1, CapturedAt.UtcTicks, 1, 1, 1, 1))
        };
        var disk = new PhysicalDiskIoAttributionSnapshot(CapturedAt,
            new Dictionary<int, PhysicalDiskProcessAttribution> { [4242] = new(4242, 10 * throughputMultiplier, 20 * throughputMultiplier, 1, 1) { ProcessStartKey = 123456 } },
            new PhysicalDiskIoProviderState("fixture", throughputState ?? (available ? PhysicalDiskIoProviderStates.Running : PhysicalDiskIoProviderStates.Unavailable), ""));
        var network = new NetworkAttributionSnapshot(CapturedAt,
            new Dictionary<int, NetworkProcessAttribution> { [4242] = new(4242, 10 * throughputMultiplier, 20 * throughputMultiplier, 1, 1, false) },
            new NetworkAttributionProviderState("fixture", throughputState ?? (available ? NetworkAttributionProviderStates.Running : NetworkAttributionProviderStates.Unavailable), ""));
        var bars = metricIds.Select(id => WindowsResourceBreakdownSampler.CreateBar(id,
            ResourceBreakdownScaleModes.Normalize(id, null), attribution, scores, hardware,
            ProcessGpuBreakdownRead.NotRequested, disk, network, null, new StaticResourceResidualBreakdownProvider())
            ?? throw new InvalidOperationException($"No production builder for {id}")).ToArray();
        return new ResourceBreakdownSnapshot(CapturedAt, bars)
        {
            Datasets = metricIds.Select(SamplingDatasetIds.ForProcessMetric).Distinct().Select(id =>
                new ResourceBreakdownDatasetSamplingState(id, ResourceBreakdownSamplingStatuses.Ready,
                    1, 1, CapturedAt, CapturedAt, CapturedAt, CapturedAt.AddSeconds(5), null, null)).ToArray()
        };
    }
}
