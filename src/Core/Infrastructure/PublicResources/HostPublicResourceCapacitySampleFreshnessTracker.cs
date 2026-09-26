using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;

namespace ResourceManager.App.Infrastructure.PublicResources;

internal sealed class HostPublicResourceCapacitySampleFreshnessTracker
{
    private DatasetStamp? memoryStamp;
    private IReadOnlyList<DatasetStamp> videoMemoryStamps = [];

    internal HostPublicResourceCapacitySampleFreshness Observe(
        HardwareMetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var memoryFresh = TryCreateStamp(
                snapshot,
                SamplingDatasetIds.SystemMemoryUsage,
                out var nextMemory)
            && ObserveStamp(ref memoryStamp, nextMemory);
        var videoMemoryFresh = TryCreateVideoMemoryStamps(
                snapshot,
                out var nextVideoMemory)
            && ObserveStamps(ref videoMemoryStamps, nextVideoMemory);
        return new HostPublicResourceCapacitySampleFreshness(
            memoryFresh,
            videoMemoryFresh);
    }

    private static bool TryCreateStamp(
        HardwareMetricSnapshot snapshot,
        string datasetId,
        out DatasetStamp stamp)
    {
        if (!snapshot.TryGetCurrentDataset(datasetId, out var observation))
        {
            stamp = default;
            return false;
        }

        stamp = DatasetStamp.From(observation);
        return true;
    }

    private static bool TryCreateVideoMemoryStamps(
        HardwareMetricSnapshot snapshot,
        out IReadOnlyList<DatasetStamp> stamps)
    {
        if (!TryCreateStamp(
                snapshot,
                SamplingDatasetIds.SystemGpuInventory,
                out var inventory))
        {
            stamps = [];
            return false;
        }

        var result = new List<DatasetStamp> { inventory };
        foreach (var adapter in snapshot.GpuInventory.Adapters
                     .Where(static adapter => adapter.CapabilityMask.HasFlag(
                         SchedulingGpuCapabilityMask.DedicatedMemory))
                     .OrderBy(static adapter => adapter.Index))
        {
            if (!TryCreateStamp(
                    snapshot,
                    SamplingDatasetIds.ForSystemMetric(
                        $"gpu.{adapter.Index}.vram"),
                    out var capacity))
            {
                stamps = [];
                return false;
            }
            result.Add(capacity);
        }

        stamps = result;
        return true;
    }

    private static bool ObserveStamp(
        ref DatasetStamp? current,
        DatasetStamp next)
    {
        if (current == next)
        {
            return false;
        }

        current = next;
        return true;
    }

    private static bool ObserveStamps(
        ref IReadOnlyList<DatasetStamp> current,
        IReadOnlyList<DatasetStamp> next)
    {
        if (current.SequenceEqual(next))
        {
            return false;
        }

        current = next.ToArray();
        return true;
    }

    private readonly record struct DatasetStamp(
        string DatasetId,
        ulong WorkspaceIdentity,
        ulong ConfigurationGeneration,
        ulong CatalogGeneration,
        ulong CommittedGeneration,
        long ObservedAtUtcTicks)
    {
        internal static DatasetStamp From(
            HardwareMetricDatasetObservation observation)
            => new(
                observation.DatasetId,
                observation.WorkspaceIdentity,
                observation.ConfigurationGeneration,
                observation.CatalogGeneration,
                observation.CommittedGeneration,
                observation.ObservedAtUtcTicks);
    }
}

internal readonly record struct HostPublicResourceCapacitySampleFreshness(
    bool Memory,
    bool VideoMemory);
