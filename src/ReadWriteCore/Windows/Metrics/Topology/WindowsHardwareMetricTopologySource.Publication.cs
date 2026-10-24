using ResourceManager.App.Infrastructure.Monitoring;

namespace ResourceManager.ReadWrite.Windows.Metrics.Topology;

public sealed partial class WindowsHardwareMetricTopologySource
{
    private NativeMetricSnapshotCatalogTopologySnapshot Publish(NativeMetricSnapshotCatalogTopologySnapshot next)
    {
        var generation = published is not null && SameContent(published, next)
            ? published.Generation : checked((published?.Generation ?? 0) + 1);
        var storageGeneration = published is not null && SameOrdinals(next.StorageSensors, published.StorageSensors)
            ? published.StorageGeneration : generation;
        var fanGeneration = published is not null && SameOrdinals(next.SystemFans, published.SystemFans)
            ? published.FanGeneration : generation;
        next = next with
        {
            Generation = generation,
            StorageGeneration = storageGeneration,
            FanGeneration = fanGeneration,
            StorageSensors = StampOrdinals(next.StorageSensors, published?.StorageSensors, storageGeneration),
            SystemFans = StampOrdinals(next.SystemFans, published?.SystemFans, fanGeneration)
        };
        // Directory references belong to the capturing operation and its holder, not this content comparison.
        published = next with { IgclFanDirectoryReference = null, NpuEngineDirectoryReference = null };
        return next;
    }

    internal static bool SameOrdinals(IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> left,
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> right)
        => left.Select(static row => row with { TopologyGeneration = 0 })
            .SequenceEqual(right.Select(static row => row with { TopologyGeneration = 0 }));

    internal static IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> StampOrdinals(
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> next,
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity>? prior, ulong generation)
        => prior is not null && SameOrdinals(next, prior) ? prior
            : next.Select(row => row with { TopologyGeneration = generation }).ToArray();

    internal static bool SameContent(NativeMetricSnapshotCatalogTopologySnapshot left,
        NativeMetricSnapshotCatalogTopologySnapshot right)
        => Metadata(left) == Metadata(right)
            && left.GpuAdapters.Count == right.GpuAdapters.Count
            && left.GpuAdapters.Zip(right.GpuAdapters).All(static pair =>
                pair.First with { PhysicalPnpInstanceIds = pair.Second.PhysicalPnpInstanceIds } == pair.Second
                && pair.First.PhysicalPnpInstanceIds.SequenceEqual(pair.Second.PhysicalPnpInstanceIds))
            && SameOrdinals(left.StorageSensors, right.StorageSensors)
            && SameOrdinals(left.SystemFans, right.SystemFans)
            && left.AmdCcdIdentities.Count == right.AmdCcdIdentities.Count
            && left.AmdCcdIdentities.Zip(right.AmdCcdIdentities).All(static pair =>
                pair.First.SensorIndex == pair.Second.SensorIndex && pair.First.SmnAddress == pair.Second.SmnAddress
                && pair.First.Target with { ProcessorAddresses = pair.Second.Target.ProcessorAddresses } == pair.Second.Target
                && pair.First.Target.ProcessorAddresses.SequenceEqual(pair.Second.Target.ProcessorAddresses))
            && left.NpuEngineIdentities.SequenceEqual(right.NpuEngineIdentities)
            && left.NpuMemoryIdentities.SequenceEqual(right.NpuMemoryIdentities)
            && left.IntelCpuIdentities.Count == right.IntelCpuIdentities.Count
            && left.IntelCpuIdentities.Zip(right.IntelCpuIdentities).All(static pair =>
                pair.First with { ProcessorAddresses = pair.Second.ProcessorAddresses } == pair.Second
                && pair.First.ProcessorAddresses.SequenceEqual(pair.Second.ProcessorAddresses))
            && left.NetworkInterfaces.SequenceEqual(right.NetworkInterfaces)
            && left.Batteries.SequenceEqual(right.Batteries)
            && left.NumaNodes.SequenceEqual(right.NumaNodes);

    private static NativeMetricSnapshotCatalogTopologySnapshot Metadata(NativeMetricSnapshotCatalogTopologySnapshot value)
        => value with
        {
            Generation = 0, StorageGeneration = 0, FanGeneration = 0, GpuAdapters = [], StorageSensors = [], SystemFans = [],
            AmdCcdIdentities = [], NpuEngineIdentities = [], NpuMemoryIdentities = [], IntelCpuIdentities = [],
            NetworkInterfaces = [], Batteries = [], NumaNodes = [],
            IgclFanDirectoryReference = null, NpuEngineDirectoryReference = null
        };
}
