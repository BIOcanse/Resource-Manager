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
using Microsoft.Extensions.Logging.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed partial class BackendFrontendWireAcceptanceTests
{
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
        // The source is never started. Only its evidence read is replaced; the real provider still
        // filters, groups, scales and constructs both ETW and remaining system-residual rows.
        using var source = new DxgkrnlVidMmEtwTelemetryZone(NullLogger<DxgkrnlVidMmEtwTelemetryZone>.Instance);
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
        Assert.All(snapshot.Bars.SelectMany(bar => bar.Software)
            .Where(software => software.SoftwareId == RuntimeAttributionIds.ResourceManagerSelf),
            software => Assert.Empty(software.Name));
        var softwareIds = snapshot.Bars.SelectMany(bar => bar.Software)
            .Select(software => software.SoftwareId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // These are the same three payload factories used by CompileResourceSubscription
        // and CompileResourceFactsSubscription. Unavailable bars are omitted by monitor wire v8.
        var wire = ResourceBreakdownWireSnapshot.Create(snapshot, metricIds, softwareIds);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(wire, WebJson));
        Assert.All(json.RootElement.GetProperty("bars").EnumerateArray(),
            bar => Assert.False(bar.TryGetProperty("label", out _)));
        var monitor = await DecodeAsync("monitor", new ResourceMonitorWireSnapshot(
            ResourceMonitorWireSnapshot.CurrentVersion, wire.CapturedAt, wire), metricIds);
        AssertDecodedMetrics(monitor, available ? metricIds : [], metricIds.Length);

        // Facts retain metrics with null totals. The production facts decoder must accept them.
        var facts = await DecodeAsync("facts", ResourceFactsSnapshot.Create(snapshot, metricIds), metricIds);
        AssertDecodedMetrics(facts, metricIds, metricIds.Length);
        var details = await DecodeAsync("details", ResourceProcessDetailsWireSnapshot.Create(
            snapshot, metricIds, softwareIds), metricIds);
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

    private static ResourceBreakdownSnapshot BuildSamplerBars(string[] metricIds, bool available)
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
            new Dictionary<int, PhysicalDiskProcessAttribution> { [4242] = new(4242, 10, 20, 1, 1) { ProcessStartKey = 123456 } },
            new PhysicalDiskIoProviderState("fixture", available ? PhysicalDiskIoProviderStates.Running : PhysicalDiskIoProviderStates.Unavailable, ""));
        var network = new NetworkAttributionSnapshot(CapturedAt,
            new Dictionary<int, NetworkProcessAttribution> { [4242] = new(4242, 10, 20, 1, 1, false) },
            new NetworkAttributionProviderState("fixture", available ? NetworkAttributionProviderStates.Running : NetworkAttributionProviderStates.Unavailable, ""));
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
