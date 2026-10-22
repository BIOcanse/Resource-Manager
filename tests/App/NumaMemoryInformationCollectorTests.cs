using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Pdh;

namespace Resource_Manager_APP.Tests;

public sealed partial class NumaMemoryInformationCollectorTests
{
    private const string FieldName = "memory_all_numa_node_available_bytes";
    private const string CapacityFieldName = "memory_all_numa_node_capacity_mib";

    [Fact]
    public void DirectoryDistinguishesFailureEmptyAndMemoryOnlyNodesWithoutTruncatingNumbers()
    {
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, WindowsMemoryMonitoringZone.ProjectNumaIdentities(null).Status);
        var empty = WindowsMemoryMonitoringZone.ProjectNumaIdentities([]);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, empty.Status);
        Assert.Empty(empty.Identities);
        var nodes = WindowsMemoryMonitoringZone.ProjectNumaIdentities([new(9, []), new(0, [0, 1]), new(ushort.MaxValue, [])]);
        Assert.Equal(new ushort[] { 0, 9, ushort.MaxValue }, nodes.Identities.Select(static node => node.NodeNumber));
        Assert.Equal("windows-numa-node|65535", nodes.Identities[2].ExactIdentityKey);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            WindowsMemoryMonitoringZone.ProjectNumaIdentities([new(65536, [])]).Status);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            WindowsMemoryMonitoringZone.ProjectNumaIdentities([new(9, []), new(9, [])]).Status);
    }

    [Fact]
    public void ReadSelectsOnlyKnownRequestedNodesAndPreservesZeroLargeValuesAndFailures()
    {
        var read = new List<ushort>();
        var zone = new WindowsMemoryMonitoringZone(node => { read.Add(node); return node switch { 0 => 0UL, 9 => ulong.MaxValue, _ => null }; });
        NumaMemoryCatalogIdentity[] nodes = [new(0), new(9), new(30)];
        Assert.Empty(zone.ReadNumaAvailable(nodes, new HashSet<string>()));
        Assert.Empty(read);
        var values = zone.ReadNumaAvailable(nodes, nodes.Select(static node => node.ExactIdentityKey)
            .Append("windows-numa-node|missing").ToHashSet(StringComparer.Ordinal));
        Assert.Equal(new ushort[] { 0, 9, 30 }, read);
        Assert.Equal(0UL, values[nodes[0].ExactIdentityKey]);
        Assert.Equal(ulong.MaxValue, values[nodes[1].ExactIdentityKey]);
        Assert.False(values.ContainsKey(nodes[2].ExactIdentityKey));
        zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        Assert.Empty(zone.ReadNumaAvailable(nodes, new HashSet<string> { nodes[1].ExactIdentityKey }));
        Assert.Equal(3, read.Count);
    }

    [Fact]
    public void CapacityReadsOnlyRequestedOsNodeNumbersAndAcceptsValidBinaryMiBValues()
    {
        var read = new List<ushort>();
        var zone = new WindowsMemoryMonitoringZone(_ => null, readNumaCapacityMiB: selected =>
        {
            read.AddRange(selected);
            return selected.ToDictionary(static node => node, static node => node switch
            {
                0 => 0UL,
                9 => (ulong)uint.MaxValue,
                _ => 42UL
            });
        });
        var topology = WindowsMemoryMonitoringZone.ProjectNumaIdentities([new(9, []), new(0, [0, 1]), new(30, [])]);
        Assert.Equal(new ushort[] { 0, 9, 30 }, topology.Identities.Select(static node => node.NodeNumber));
        var requested = new HashSet<string> { topology.Identities[0].ExactIdentityKey, topology.Identities[1].ExactIdentityKey,
            topology.Identities[2].ExactIdentityKey, "windows-numa-node|missing" };
        var values = zone.ReadNumaCapacityMiB(topology.Identities, requested);
        Assert.Equal(new ushort[] { 0, 9, 30 }, read);
        Assert.Equal(0UL, values["windows-numa-node|0"]);
        Assert.Equal((ulong)uint.MaxValue, values["windows-numa-node|9"]);
        Assert.Equal(42UL, values["windows-numa-node|30"]);
        Assert.False(values.ContainsKey("windows-numa-node|missing"));
    }

    [Fact]
    public void CapacityCounterPathAndPdhProjectionRejectInvalidTypeStatusAndRange()
    {
        Assert.Equal(@"\NUMA Node Memory(9)\Total MBytes", WindowsMemoryMetricResource.CreateNumaCapacityCounterPath(9));
        Assert.True(WindowsMemoryMetricResource.TryProjectNumaCapacityMiB(PdhCounterMethods.ErrorSuccess,
            PdhCounterMethods.PdhCounterTypeRawCount,
            new PdhFmtCounterValue { CStatus = PdhCounterMethods.PdhCStatusValidData, LargeValue = 0 }, out var zero));
        Assert.Equal(0UL, zero);
        Assert.True(WindowsMemoryMetricResource.TryProjectNumaCapacityMiB(PdhCounterMethods.ErrorSuccess,
            PdhCounterMethods.PdhCounterTypeRawCount,
            new PdhFmtCounterValue { CStatus = PdhCounterMethods.PdhCStatusNewData, LargeValue = uint.MaxValue }, out var maximum));
        Assert.Equal((ulong)uint.MaxValue, maximum);
        Assert.False(WindowsMemoryMetricResource.TryProjectNumaCapacityMiB(-1, PdhCounterMethods.PdhCounterTypeRawCount,
            new PdhFmtCounterValue { CStatus = PdhCounterMethods.PdhCStatusValidData, LargeValue = 1 }, out _));
        Assert.False(WindowsMemoryMetricResource.TryProjectNumaCapacityMiB(PdhCounterMethods.ErrorSuccess, 0,
            new PdhFmtCounterValue { CStatus = PdhCounterMethods.PdhCStatusValidData, LargeValue = 1 }, out _));
        Assert.False(WindowsMemoryMetricResource.TryProjectNumaCapacityMiB(PdhCounterMethods.ErrorSuccess,
            PdhCounterMethods.PdhCounterTypeRawCount, new PdhFmtCounterValue { CStatus = 2, LargeValue = 1 }, out _));
        Assert.False(WindowsMemoryMetricResource.TryProjectNumaCapacityMiB(PdhCounterMethods.ErrorSuccess,
            PdhCounterMethods.PdhCounterTypeRawCount, new PdhFmtCounterValue { CStatus = PdhCounterMethods.PdhCStatusValidData, LargeValue = -1 }, out _));
        Assert.False(WindowsMemoryMetricResource.TryProjectNumaCapacityMiB(PdhCounterMethods.ErrorSuccess,
            PdhCounterMethods.PdhCounterTypeRawCount, new PdhFmtCounterValue { CStatus = PdhCounterMethods.PdhCStatusValidData, LargeValue = (long)uint.MaxValue + 1 }, out _));
    }

    [Fact]
    public void DefinitionAndCompiledRuleUseExplicitUInt64NodeScopeAndOnlyCanonicalAddresses()
    {
        var field = MetricFieldRegistry.FindByName(FieldName)!;
        Assert.Equal(MetricResource.Memory, field.Resource);
        Assert.Equal(MetricVendor.All, field.Vendor);
        Assert.Equal("B", field.Unit);
        Assert.Equal("unsigned64", field.ValueKind);
        Assert.Contains("standby", field.Meaning);
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var capacityField = MetricFieldRegistry.FindByName(CapacityFieldName)!;
        Assert.Equal(MetricResource.Memory, capacityField.Resource);
        Assert.Equal(MetricVendor.All, capacityField.Vendor);
        Assert.Equal("MiB", capacityField.Unit);
        Assert.Equal("unsigned64", capacityField.ValueKind);
        Assert.Contains("NumberOfItems32", capacityField.Meaning);
        Assert.Contains("binary MiB", capacityField.Meaning);
        Assert.Contains("installed DIMM capacity", capacityField.Meaning);
        var capacityRule = Assert.Single(compiled.HotPublish.RuleTemplates.Where(rule => rule.TemplateId == $"{CapacityFieldName}.optional"));
        Assert.Equal("numa.{numa}.capacityMiB", capacityRule.MetricIdTemplate);
        Assert.Equal(9U, capacityRule.ScopeExpansion); Assert.Equal(3U, capacityRule.ScopeKind); Assert.Equal(3U, capacityRule.ValueKind);
        Assert.Equal(64U, capacityRule.MaximumInstanceCount); Assert.Equal(ulong.MaxValue, capacityRule.MaximumValueBits);
        var rule = Assert.Single(compiled.HotPublish.RuleTemplates.Where(rule => rule.TemplateId == $"{FieldName}.optional"));
        Assert.Equal(9U, rule.ScopeExpansion); Assert.Equal(3U, rule.ScopeKind); Assert.Equal(3U, rule.ValueKind);
        Assert.Equal(64U, rule.MaximumInstanceCount); Assert.Equal(ulong.MaxValue, rule.MaximumValueBits);
        var first = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty,
            numaNodes: [new(9), new(0)]);
        var bindings = first.MetricBindings.Values.Where(static binding => binding.FieldCode == MetricFieldCode.NumaNodeAvailableBytes).ToArray();
        Assert.Equal(2, bindings.Length);
        Assert.All(bindings, binding => { Assert.EndsWith($"/{FieldName}", binding.MetricId); Assert.Empty(binding.LegacyMetricId); });
        Assert.DoesNotContain("numa.9.availableBytes", first.MetricHandles.Keys);
        var capacityBindings = first.MetricBindings.Values.Where(static binding => binding.FieldCode == MetricFieldCode.NumaNodeCapacityMiB).ToArray();
        Assert.Equal(2, capacityBindings.Length);
        Assert.All(capacityBindings, binding =>
        {
            Assert.EndsWith($"/{CapacityFieldName}", binding.MetricId);
            Assert.Empty(binding.LegacyMetricId);
        });
        Assert.DoesNotContain(string.Empty, first.LegacyMetricHandles.Keys);
        var reordered = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], first.HandleMap,
            numaNodes: [new(0), new(9)]);
        Assert.True(HostManagerMetricSnapshotOwner.CanRetainCatalog(first, reordered));
        Assert.All(bindings, binding => Assert.Equal(binding.MetricHandle, reordered.MetricHandles[binding.MetricId]));
        var changed = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], first.HandleMap, numaNodes: [new(0), new(30)]);
        Assert.False(HostManagerMetricSnapshotOwner.CanRetainCatalog(first, changed));
        Assert.DoesNotContain(changed.MetricBindings.Values, static binding => binding.DeviceIdentity == "windows-numa-node|9");
    }

    [Fact]
    public void NodeExpansionUsesConfiguredCapacityAndRejectsDuplicates()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        Assert.Throws<InvalidOperationException>(() => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty, numaNodes: [new(0), new(0)]));
        Assert.Throws<InvalidOperationException>(() => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty, numaNodes: Enumerable.Range(0, 65).Select(static number => new NumaMemoryCatalogIdentity((ushort)number)).ToArray()));
        var limited = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var templates = root["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
            templates.Single(template => template!["template_id"]!.GetValue<string>() == $"{FieldName}.optional")!["maximum_instance_count"] = 1;
        }).MetricSnapshot;
        Assert.Throws<InvalidOperationException>(() => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(limited, [], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty, numaNodes: [new(0), new(1)]));
    }

    [Fact]
    public void FailedDirectoryRetainsDeclarationsAndSuccessfulEmptyRemovesThem()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty, numaNodes: [new(9)]);
        var topology = Topology([]);
        Assert.Single(HostManagerMetricSnapshotOwner.RetainUnavailableOptionalIdentities(topology with
            { NumaStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable }, catalog).NumaNodes);
        var successful = HostManagerMetricSnapshotOwner.RetainUnavailableOptionalIdentities(topology, catalog);
        Assert.Empty(successful.NumaNodes);
        var removed = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], catalog.HandleMap, numaNodes: successful.NumaNodes);
        Assert.DoesNotContain(removed.MetricBindings.Values, static binding => binding.FieldCode is MetricFieldCode.NumaNodeAvailableBytes or MetricFieldCode.NumaNodeCapacityMiB);
        var failedSnapshot = WindowsHardwareMetricSampler.AddCatalogObservations(new HardwareMetricSnapshot(
            DateTimeOffset.UtcNow, null!, null!, null!, [], null!, new Dictionary<string, MetricValue>()),
            topology with { NumaStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable }, DateTimeOffset.UtcNow);
        Assert.Equal(SamplingObservationStatus.Unavailable, failedSnapshot.Datasets[SamplingDatasetIds.NumaInventory].Status);
    }

    [Fact]
    public void ProductionWindowsDirectoryAndReadUseActualNumaApi()
    {
        var topology = WindowsMemoryMonitoringZone.ReadNumaIdentities();
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, topology.Status);
        Assert.NotEmpty(topology.Identities);
        var requested = topology.Identities.Select(static node => node.ExactIdentityKey).ToHashSet(StringComparer.Ordinal);
        var values = new WindowsMemoryMonitoringZone().ReadNumaAvailable(topology.Identities, requested);
        Assert.Equal(requested.Count, values.Count);
    }

    private static NativeMetricSnapshotCatalogTopologySnapshot Topology(IReadOnlyList<NumaMemoryCatalogIdentity> nodes)
        => new(1, NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
            NativeMetricSnapshotCatalogTopologyStatus.Complete, []) { NumaStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete, NumaNodes = nodes };
}
