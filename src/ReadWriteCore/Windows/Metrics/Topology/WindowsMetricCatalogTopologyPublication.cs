using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
namespace ResourceManager.ReadWrite.Windows.Metrics.Topology;
public static class WindowsMetricCatalogTopologyPublication
{
    public static void RequireGpuTopology(NativeMetricSnapshotCatalogTopologySnapshot topology)
    {
        if (topology.GpuStatus != NativeMetricSnapshotCatalogTopologyStatus.Complete || topology.Generation == 0)
            throw new InvalidOperationException("The required exact GPU topology is unavailable.");
    }

    public static NativeMetricSnapshotCatalogProjection ProjectCatalog(NativeMetricSnapshotCatalogConfiguration configuration,
        NativeMetricSnapshotCatalogTopologySnapshot topology, NativeMetricSnapshotCatalogHandleMap handleMap)
        => NativeMetricSnapshotCatalogProjection.Create(configuration, topology.GpuAdapters,
            topology.StorageSensors, topology.SystemFans, handleMap, topology.CpuVendor,
            topology.Batteries, topology.NumaNodes, topology.IntelCpuIdentities, topology.NetworkInterfaces, topology.NpuMemoryIdentities, topology.NpuEngineIdentities, topology.NpuEngineDirectory, topology.AmdCcdIdentities);
    public static NativeMetricSnapshotCatalogTopologySnapshot RetainUnavailableOptionalIdentities(
        NativeMetricSnapshotCatalogTopologySnapshot topology,
        NativeMetricSnapshotCatalogProjection prior)
        => topology with
        {
            AmdCcdIdentities = topology.AmdCcdStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable
                ? prior.AmdCcdIdentities : topology.AmdCcdIdentities,
            NpuEngineIdentities = topology.NpuEngineStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable
                ? prior.NpuEngineIdentities : topology.NpuEngineIdentities,
            NpuEngineDirectory = topology.NpuEngineStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable
                ? prior.NpuEngineDirectory : topology.NpuEngineDirectory,
            NpuMemoryIdentities = topology.NpuMemoryStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable
                ? prior.NpuMemoryIdentities : topology.NpuMemoryIdentities,
            StorageSensors = topology.StorageStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable
                ? prior.StorageIdentities : topology.StorageSensors,
            SystemFans = RetainFans(topology, prior.SystemFanIdentities),
            Batteries = topology.BatteryStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable
                ? prior.BatteryIdentities : topology.Batteries,
            NetworkInterfaces = topology.NetworkInterfaceStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable
                ? prior.NetworkInterfaceIdentities : topology.NetworkInterfaces,
            NumaNodes = topology.NumaStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable
                ? prior.NumaIdentities : topology.NumaNodes,
            IntelCpuIdentities = topology.IntelCpuIdentities
                .Where(identity => IntelStatus(topology, identity.Kind)
                    != NativeMetricSnapshotCatalogTopologyStatus.Unavailable)
                .Concat(prior.IntelCpuIdentities.Where(identity =>
                    IntelStatus(topology, identity.Kind)
                        == NativeMetricSnapshotCatalogTopologyStatus.Unavailable)).ToArray()
        };

    private static IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> RetainFans(
        NativeMetricSnapshotCatalogTopologySnapshot topology,
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> prior)
    {
        // Each source owns its unknown/empty result. OEM channels remain explicit configuration.
        var wmi = topology.FanStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable ? prior : topology.SystemFans;
        var nvml = topology.NvmlFanStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable ? prior : topology.SystemFans;
        var igcl = topology.IgclFanStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable ? prior : topology.SystemFans;
        var oem = topology.NotebookOemFanStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable ? prior : topology.SystemFans;
        return wmi.Where(static fan => fan.NotebookOemRole is null && fan.NvmlFan is null && fan.IgclFan is null)
            .OrderBy(static fan => fan.DisplayIndex)
            .Concat(oem.Where(static fan => fan.NotebookOemRole is not null && fan.NvmlFan is null && fan.IgclFan is null)
                .OrderBy(static fan => fan.DisplayIndex))
            .Concat(nvml.Where(static fan => fan.NvmlFan is not null).OrderBy(static fan => fan.ExactIdentityKey, StringComparer.Ordinal))
            .Concat(igcl.Where(static fan => fan.IgclFan is not null).OrderBy(static fan => fan.ExactIdentityKey, StringComparer.Ordinal))
            .Select((fan, index) => fan with { DisplayIndex = index, TopologyGeneration = topology.Generation }).ToArray();
    }

    private static NativeMetricSnapshotCatalogTopologyStatus IntelStatus(
        NativeMetricSnapshotCatalogTopologySnapshot topology, IntelMsrTargetKind kind) => kind switch
    {
        IntelMsrTargetKind.PhysicalCore => topology.IntelCpuCoreStatus,
        IntelMsrTargetKind.Package => topology.IntelCpuPackageStatus,
        IntelMsrTargetKind.LogicalProcessor => topology.IntelCpuLogicalProcessorStatus,
        IntelMsrTargetKind.PhysicalDie => topology.IntelCpuDieStatus,
        IntelMsrTargetKind.Platform => topology.IntelPlatformStatus,
        _ => throw new InvalidOperationException("Invalid Intel MSR target kind.")
    };

    public static bool CanRetainCatalog(NativeMetricSnapshotCatalogProjection prior,
        NativeMetricSnapshotCatalogProjection desired)
        => ReferenceEquals(prior.NpuEngineDirectory, desired.NpuEngineDirectory)
            && prior.CatalogIdentitySha256 == desired.CatalogIdentitySha256
            && prior.GpuScopeBindings.Count == desired.GpuScopeBindings.Count
            && prior.GpuScopeBindings.All(pair => desired.GpuScopeBindings.TryGetValue(pair.Key, out var next)
                && pair.Value.AdapterLuid == next.AdapterLuid
                && pair.Value.InventoryGeneration == next.InventoryGeneration
                && pair.Value.DisplayIndex == next.DisplayIndex
                && pair.Value.DisplayName == next.DisplayName);

}

