using System.Collections.Immutable;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;

namespace Resource_Manager_APP.Tests;

public sealed class NpuMemoryOwnerContractTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void DirectoryCapacityRefusesTheWholeOverflowAndSuccessfulEmptyRetires(int capacity)
    {
        var rows = Rows(capacity);
        var complete = WindowsNpuMemoryTopology.Project(rows, (uint)capacity);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, complete.Status);
        Assert.Equal(capacity, complete.Identities.Count);
        var overflow = WindowsNpuMemoryTopology.Project(Rows(capacity + 1), (uint)capacity);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, overflow.Status);
        Assert.Empty(overflow.Identities);
        var config = Configuration(capacity).CatalogConfiguration;
        var prior = Catalog(config, complete.Identities);
        Assert.Equal(capacity * 4, NpuBindings(prior).Length);
        var unavailable = Topology(overflow);
        var retained = WindowsMetricCatalogTopologyPublication.RetainUnavailableOptionalIdentities(unavailable, prior);
        var retainedCatalog = WindowsMetricCatalogTopologyPublication.ProjectCatalog(config, retained, prior.HandleMap);
        Assert.Equal(prior.NpuMemoryIdentities.Select(row => row.ExactIdentityKey), retainedCatalog.NpuMemoryIdentities.Select(row => row.ExactIdentityKey));
        Assert.Equal(NpuBindings(prior).Select(row => row.MetricHandle).Order(), NpuBindings(retainedCatalog).Select(row => row.MetricHandle).Order());
        var empty = WindowsMetricCatalogTopologyPublication.RetainUnavailableOptionalIdentities(
            Topology(new(NativeMetricSnapshotCatalogTopologyStatus.Complete, [])), prior);
        Assert.Empty(NpuBindings(WindowsMetricCatalogTopologyPublication.ProjectCatalog(config, empty, prior.HandleMap)));
    }

    [Fact]
    public void EveryApplicableTemplateParticipatesAndNoTemplateMeansNoDirectoryQuery()
    {
        var original = Configuration(64).CatalogConfiguration;
        var npu = original.RuleTemplates.Where(row => row.SourceId == NativeMetricSnapshotSources.WindowsNpuMemory).ToArray();
        var narrowerDuplicateField = npu[0] with { TemplateId = npu[0].TemplateId + ".narrow", MaximumInstanceCount = 1 };
        Assert.Equal(1U, WindowsNpuMemoryTopology.EffectiveMaximumInstanceCount(original.RuleTemplates.Add(narrowerDuplicateField)));
        Assert.Equal(64U, WindowsNpuMemoryTopology.EffectiveMaximumInstanceCount(original.RuleTemplates));
        Assert.Equal(0U, WindowsNpuMemoryTopology.EffectiveMaximumInstanceCount(original.RuleTemplates.Where(row => row.SourceId != NativeMetricSnapshotSources.WindowsNpuMemory)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public async Task OwnerPassesCompiledCapacityAndUnavailablePreservesDefinitionsButClearsCurrent(int capacity)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var rows = Rows(capacity);
        var directoryBounds = new List<uint>();
        var queryBounds = new List<uint>();
        var resources = RecordedResources(
            bound => { directoryBounds.Add(bound); return WindowsNpuMemoryTopology.Project(rows, bound); },
            (devices, requested, bound, _) =>
            {
                queryBounds.Add(bound);
                Assert.Equal(capacity, devices.Count);
                Assert.Equal(capacity, requested.Count);
                Assert.All(requested.Values, fields => Assert.Equal(NpuMemoryFields.DedicatedCommitted, fields));
                return devices.Select(device => new NpuMemoryRead(device.ExactIdentityKey, 9007199254740993UL, null, null, null)).ToArray();
            });
        var configuration = Configuration(capacity);
        using var owner = new WindowsHardwareMetricOwner(configuration, resources, new());
        var bindings = NpuBindings(Catalog(configuration.CatalogConfiguration, WindowsNpuMemoryTopology.Project(rows, (uint)capacity).Identities))
            .Where(row => row.FieldCode == MetricFieldCode.NpuDedicatedMemoryCommitted).ToArray();
        var request = MetricSampleRequest.ForIds(bindings.Select(row => row.MetricId)).WithCatalog();
        var first = await owner.CaptureAsync(request, timeout.Token);
        var cached = Assert.IsType<HardwareMetricSnapshot>(owner.ReadCachedSnapshot());
        Assert.All(bindings, binding => Assert.Equal("9007199254740993", cached.Items[binding.MetricId].IntegerValue));
        Assert.All(bindings, binding => Assert.Equal("9007199254740993", first.Items[binding.MetricId].IntegerValue));
        Assert.Equal(new[] { (uint)capacity }, directoryBounds);
        Assert.Equal(new[] { (uint)capacity }, queryBounds);
        rows = Rows(capacity + 1);
        var unavailable = await owner.CaptureAsync(request, timeout.Token);
        Assert.All(bindings, binding =>
        {
            Assert.Contains(binding.MetricId, unavailable.FieldBindings.Keys);
            Assert.Null(unavailable.Items[binding.MetricId].IntegerValue);
            Assert.Null(unavailable.Items[binding.MetricId].NumericValue);
        });
        Assert.Single(queryBounds);
        Assert.Equal(new[] { (uint)capacity, (uint)capacity }, directoryBounds);
        rows = [];
        var empty = await owner.CaptureAsync(request, timeout.Token);
        Assert.DoesNotContain(empty.FieldBindings.Values, binding => binding.SourceId == NativeMetricSnapshotSources.WindowsNpuMemory);
        Assert.Single(queryBounds);
    }

    [Fact]
    public async Task OwnerWithoutNpuTemplatesNeverCallsDirectoryOrMemoryReader()
    {
        var config = Configuration(64);
        config = config with { CatalogConfiguration = config.CatalogConfiguration with
        {
            RuleTemplates = config.CatalogConfiguration.RuleTemplates.Where(row => row.SourceId != NativeMetricSnapshotSources.WindowsNpuMemory).ToImmutableArray()
        }};
        var queries = 0;
        using var owner = new WindowsHardwareMetricOwner(config, RecordedResources(
            _ => { queries++; throw new InvalidOperationException("No template permits enumeration."); },
            (_, _, _, _) => { queries++; throw new InvalidOperationException("No template permits sampling."); }), new());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await owner.CaptureAsync(MetricSampleRequest.CatalogProbe, timeout.Token);
        Assert.Equal(0, queries);
    }

    private static WindowsMetricConfiguration Configuration(int capacity)
    {
        var profile = JsonNode.Parse(WindowsMetricConfiguration.ReadDefaultProfileBytes())!.AsObject();
        foreach (var rule in profile["hot_publish"]!["rule_templates"]!.AsArray().Where(rule => rule!["source_id"]!.GetValue<string>() == NativeMetricSnapshotSources.WindowsNpuMemory))
            rule!["maximum_instance_count"] = capacity;
        return WindowsMetricConfiguration.LoadBytes(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(profile));
    }

    private static DeviceTopologyNpuInformation[] Rows(int count) => Enumerable.Range(0, count).Select(index =>
    {
        var pnp = $@"ACPI\NPU{index:D4}\0";
        return new DeviceTopologyNpuInformation(new NpuMemoryCatalogIdentity(pnp).ExactIdentityKey, pnp, "Recorded NPU", null, null, null);
    }).ToArray();
    private static NativeMetricSnapshotCatalogProjection Catalog(NativeMetricSnapshotCatalogConfiguration config, IReadOnlyList<NpuMemoryCatalogIdentity> devices)
        => NativeMetricSnapshotCatalogProjection.Create(config, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty, npuMemoryIdentities: devices);
    private static NativeMetricSnapshotRuleCatalogBinding[] NpuBindings(NativeMetricSnapshotCatalogProjection catalog)
        => catalog.MetricBindings.Values.Where(row => row.SourceId == NativeMetricSnapshotSources.WindowsNpuMemory).ToArray();
    private static NativeMetricSnapshotCatalogTopologySnapshot Topology(NpuMemoryTopologyRead npu)
        => new(1, NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [])
        { NpuMemoryStatus = npu.Status, NpuMemoryIdentities = npu.Identities };

    // Reuses the original callback-only resource fixture pattern; no device SDK is opened.
    private static WindowsHardwareMetricResources RecordedResources(Func<uint, NpuMemoryTopologyRead> directory,
        Func<IReadOnlyList<NpuMemoryCatalogIdentity>, IReadOnlyDictionary<string, NpuMemoryFields>, uint, CancellationToken, IReadOnlyList<NpuMemoryRead>> read) => new()
    {
        CpuName = "recorded", MemoryDescription = "recorded",
        Controls = new WindowsMetricControlResources { ControlReadCpuIdentity = static () => null, ControlReadCpuTopology = static () => throw new InvalidOperationException("No CPU controls in this fixture.") },
        ReadNpuMemoryIdentities = directory, ReadNpuMemory = read,
        ReadGpuInventory = static () => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UnixEpoch.AddSeconds(1).UtcTicks, 0, 0, 0, 1, []),
        ReadStorageIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadFanIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadBatteryIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNetworkIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNumaIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadCpuCounters = static () => throw new InvalidOperationException(), ReadMemory = static () => throw new InvalidOperationException(),
        ReadVirtualMemory = static () => throw new InvalidOperationException(), ReadNumaAvailable = static (_, _) => new Dictionary<string, ulong>(),
        ReadCpuFrequency = static () => throw new InvalidOperationException(), ReadGpuUsage = static _ => throw new InvalidOperationException(),
        ReadNvmlAsync = static (_, _, _) => throw new InvalidOperationException(), ReadNvapiAsync = static (_, _, _) => throw new InvalidOperationException(),
        ReadAdlxAsync = static (_, _, _) => throw new InvalidOperationException(), ReadAmdSmu = static _ => throw new InvalidOperationException(),
        ReadIntelGpu = static (_, _, _) => throw new InvalidOperationException(), ReadIntelMsr = static (_, _) => throw new InvalidOperationException(),
        ReadBattery = static (_, _) => throw new InvalidOperationException(), ReadNetworkInterfaces = static (_, _) => throw new InvalidOperationException(),
        ReadSystemIo = static _ => throw new InvalidOperationException(), ReadStorageSensors = static () => throw new InvalidOperationException(),
        ReadNvmeSensors = static _ => throw new InvalidOperationException(), ReadAtaSensors = static _ => throw new InvalidOperationException(),
        ReadHardwareMonitor = static () => throw new InvalidOperationException(), ReadNotebookFans = static () => throw new InvalidOperationException(),
        SynchronizeNotebookDemand = static _ => { }
    };
}