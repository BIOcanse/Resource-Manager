using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class AmdCcdMetricIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalCapturePublishesOnlyRequestedCcdAndKeepsUnavailableValuesNull(bool unavailable)
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"ccd-current-{Guid.NewGuid():N}.bin"));
        var targets = Identities();
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [],
            workspace.CreateCatalogHandleMap(), MetricVendor.Amd, amdCcdIdentities: targets);
        var bindings = catalog.MetricBindings.Values.Where(binding => binding.FieldCode == MetricFieldCode.AmdCcdTdieTemperature)
            .OrderBy(binding => binding.DisplayIndex).ToArray();
        Assert.Equal(2, bindings.Length);
        Assert.All(bindings, binding => { Assert.Equal("amd.smn", binding.SourceId); Assert.Empty(binding.LegacyMetricId); });
        Assert.NotEqual(bindings[0].MetricHandle, bindings[1].MetricHandle);
        var at = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, at);
        var calls = 0;
        var resources = RecordedResources((all, requested) =>
        {
            calls++;
            Assert.Equal(targets, all);
            Assert.Equal(new[] { targets[1].ExactIdentityKey }, requested);
            return new Dictionary<string, double?> { [targets[1].ExactIdentityKey] = -0.125 };
        });
        using var ownedResources = resources;
        var capture = new WindowsHardwareMetricCapture(resources,
            TimeSpan.Zero, new AmdIntegratedGpuSensorRoute(), static () => { });
        var topology = new NativeMetricSnapshotCatalogTopologySnapshot(1,
            NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
            NativeMetricSnapshotCatalogTopologyStatus.Complete, [])
        {
            AmdCcdStatus = unavailable ? NativeMetricSnapshotCatalogTopologyStatus.Unavailable : NativeMetricSnapshotCatalogTopologyStatus.Complete,
            AmdCcdIdentities = targets
        };
        var result = await capture.CaptureUnderGateAsync(MetricSampleRequest.ForIds([bindings[1].MetricId]), workspace, 1,
            topology, action => { action(); return true; }, default);
        Assert.NotNull(result);
        Assert.Equal(unavailable ? 0 : 1, calls);
        var fields = MetricDeviceFields.Read(result!, fields: new HashSet<string> { "cpu_amd_ccd_tdie_temperature_celsius" });
        Assert.Equal(2, fields.Count);
        var selected = Assert.Single(fields, field => field.DeviceIdentity == targets[1].ExactIdentityKey);
        var value = Assert.Single(selected.Fields);
        if (unavailable) { Assert.NotEqual(SamplingObservationStatus.Current, value.Status); Assert.Null(value.NumericValue); }
        else
        {
            Assert.Equal(SamplingObservationStatus.Current, value.Status); Assert.Equal(-0.125, value.NumericValue);
            Assert.Equal("cpu.ccd_die_temperature", value.Reading!.Quantity);
        }
        Assert.Null(Assert.Single(Assert.Single(fields, field => field.DeviceIdentity == targets[0].ExactIdentityKey).Fields).NumericValue);
        var request = MetricDeviceFields.CreateRequest(result, null, ["cpu_amd_ccd_tdie_temperature_celsius"]);
        Assert.Equal(bindings.Select(binding => binding.MetricId).Order(), request.Ids!.Order());
        var wire = JsonSerializer.Serialize(fields, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        output.WriteLine("CCD-FIELDS-WIRE " + wire);
        output.WriteLine("CCD-CATALOG-WIRE " + JsonSerializer.Serialize(MetricDeviceFields.Catalog(result),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Contains("\"kind\":\"cpu_die\"", wire);
        Assert.Contains("\"sensorIdentifier\":\"amd.smn:00059B14\"", wire);
        Assert.DoesNotContain("\"processorGroup\"", wire);
    }

    [Fact]
    public void OptionalFailureRetainsDirectoryButSuccessfulEmptyRemovesItAndCapacityIsExplicit()
    {
        var config = WindowsMetricConfiguration.CreateDefault().CatalogConfiguration;
        var targets = Identities();
        var prior = NativeMetricSnapshotCatalogProjection.Create(config, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty,
            MetricVendor.Amd, amdCcdIdentities: targets);
        var topology = new NativeMetricSnapshotCatalogTopologySnapshot(1,
            NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
            NativeMetricSnapshotCatalogTopologyStatus.Complete, []);
        var retained = WindowsMetricCatalogTopologyPublication.RetainUnavailableOptionalIdentities(topology, prior);
        Assert.Equal(targets, retained.AmdCcdIdentities);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, retained.AmdCcdStatus);
        Assert.Empty(WindowsMetricCatalogTopologyPublication.RetainUnavailableOptionalIdentities(
            topology with { AmdCcdStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete }, prior).AmdCcdIdentities);
        Assert.Equal(16U, WindowsAmdCcdTopology.EffectiveMaximumInstanceCount(config.RuleTemplates));
        Assert.Throws<InvalidOperationException>(() => NativeMetricSnapshotCatalogProjection.Create(config, [], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty, amdCcdIdentities: [targets[0], targets[0]]));
        Assert.Throws<InvalidOperationException>(() => NativeMetricSnapshotCatalogProjection.Create(config, [], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty, amdCcdIdentities: [targets[0] with { SmnAddress = 0x1234 }]));
    }

    private static AmdCcdCatalogIdentity[] Identities()
    {
        var target = new AmdCcdCpuTarget("package-opaque", 0x19, 0x61, 0xA60F12, [new(0, 0), new(0, 1)]);
        return [new(target, 0, 0x59B08), new(target, 3, 0x59B14)];
    }

    private static WindowsHardwareMetricResources RecordedResources(
        Func<IReadOnlyList<AmdCcdCatalogIdentity>, IReadOnlySet<string>, IReadOnlyDictionary<string, double?>> read) => new()
    {
        ReadAmdCcd = read,
        ReadCpuCounters = () => throw new InvalidOperationException(), ReadMemory = () => throw new InvalidOperationException(),
        ReadVirtualMemory = () => throw new InvalidOperationException(), ReadNumaAvailable = (_, _) => throw new InvalidOperationException(),
        ReadNumaIdentities = () => throw new InvalidOperationException(), ReadGpuInventory = () => throw new InvalidOperationException(),
        ReadCpuFrequency = () => throw new InvalidOperationException(), ReadGpuUsage = _ => throw new InvalidOperationException(),
        ReadNvmlAsync = (_, _, _) => throw new InvalidOperationException(), ReadNvapiAsync = (_, _, _) => throw new InvalidOperationException(),
        ReadAdlxAsync = (_, _, _) => throw new InvalidOperationException(), ReadAmdSmu = _ => throw new InvalidOperationException(),
        ReadIntelGpu = (_, _, _) => throw new InvalidOperationException(), ReadIntelMsr = (_, _) => throw new InvalidOperationException(),
        ReadBatteryIdentities = () => throw new InvalidOperationException(), ReadBattery = (_, _) => throw new InvalidOperationException(),
        ReadNetworkIdentities = () => throw new InvalidOperationException(), ReadNetworkInterfaces = (_, _) => throw new InvalidOperationException(),
        ReadSystemIo = _ => throw new InvalidOperationException(), ReadStorageIdentities = _ => throw new InvalidOperationException(),
        ReadFanIdentities = _ => throw new InvalidOperationException(), ReadStorageSensors = () => throw new InvalidOperationException(),
        ReadNvmeSensors = _ => throw new InvalidOperationException(), ReadAtaSensors = _ => throw new InvalidOperationException(),
        ReadHardwareMonitor = () => throw new InvalidOperationException(), ReadNotebookFans = () => throw new InvalidOperationException(),
        SynchronizeNotebookDemand = _ => { }
    };
}
