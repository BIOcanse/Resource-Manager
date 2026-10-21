using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Domain.Monitoring;

public static class SamplingDatasetIds
{
    public const string SystemMetricCatalog = MetricDatasetIds.SystemMetricCatalog;
    public const string StorageInventory = MetricDatasetIds.StorageInventory;
    public const string FanInventory = MetricDatasetIds.FanInventory;
    public const string BatteryInventory = MetricDatasetIds.BatteryInventory;
    public const string NumaInventory = MetricDatasetIds.NumaInventory;
    public const string SystemGpuInventory = MetricDatasetIds.SystemGpuInventory;
    public const string SystemCpuUsage = MetricDatasetIds.SystemCpuUsage;
    public const string SystemMemoryUsage = MetricDatasetIds.SystemMemoryUsage;
    public const string SystemVirtualMemoryUsage = MetricDatasetIds.SystemVirtualMemoryUsage;

    public const string ProcessInventory = "process.inventory";
    public const string ProcessAttribution = "process.attribution";
    public const string ProcessRuntimeState = "process.runtime-state";
    public const string ProcessCpuUsage = "process.cpu.usage";
    public const string ProcessMemoryUsage = "process.memory.usage";
    public const string ProcessPrivateCommit = "process.private-commit";
    public const string ProcessDiskThroughput = "process.disk.throughput";
    public const string ProcessNetworkThroughput = "process.network.throughput";
    public const string ProcessRawNetworkThroughput = "process.network.raw-throughput";
    public const string ProcessObservedNetworkTraffic = ProcessMetricDatasetIds.ObservedNetworkTraffic;
    public const string ProcessObservedPhysicalDiskIo = ProcessMetricDatasetIds.ObservedPhysicalDiskIo;
    public const string ProcessBasicMetrics = ProcessMetricDatasetIds.BasicMetrics;
    public const string ProcessGpuUsage = "process.gpu.usage";
    public const string ProcessGpuVram = "process.gpu.vram";

    public static IReadOnlySet<string> ResolveAllSystemDatasets(IEnumerable<string> availableMetricIds)
        => MetricDatasetIds.ResolveAllSystemDatasets(availableMetricIds);

    public static string ForSystemMetric(string metricId)
        => MetricDatasetIds.ForSystemMetric(metricId);

    public static string ForProcessMetric(string metricId)
    {
        var normalized = Normalize(metricId);
        if (ProcessMetricFieldRegistry.Find(normalized) is { } field)
        {
            return field.DatasetId;
        }
        if (normalized.Equals(ResourceBreakdownMetricIds.CpuUsage, StringComparison.OrdinalIgnoreCase))
        {
            return ProcessCpuUsage;
        }
        if (normalized.Equals(ResourceBreakdownMetricIds.MemoryUsage, StringComparison.OrdinalIgnoreCase))
        {
            return ProcessMemoryUsage;
        }
        if (normalized.Equals(ResourceBreakdownMetricIds.VirtualMemoryUsage, StringComparison.OrdinalIgnoreCase))
        {
            return ProcessPrivateCommit;
        }
        if (normalized.StartsWith("disk.", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessDiskThroughput;
        }
        if (normalized.StartsWith("network.raw.", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessRawNetworkThroughput;
        }
        if (normalized.StartsWith("network.", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessNetworkThroughput;
        }
        if (TryParseGpuMetric(normalized, out _, out var gpuMetric))
        {
            if (gpuMetric.Equals("usage", StringComparison.OrdinalIgnoreCase))
            {
                return ProcessGpuUsage;
            }
            if (gpuMetric.Equals("vram", StringComparison.OrdinalIgnoreCase))
            {
                return ProcessGpuVram;
            }
        }

        return $"process.metric.{normalized}";
    }

    public static IReadOnlyList<string> ForSchedulingMetrics(
        SchedulingProcessMetricMask mask)
    {
        var result = new List<string>(4);
        if (mask.HasFlag(SchedulingProcessMetricMask.CpuUsage))
        {
            result.Add(ProcessCpuUsage);
        }
        if (mask.HasFlag(SchedulingProcessMetricMask.MemoryUsage))
        {
            result.Add(ProcessMemoryUsage);
        }
        if (mask.HasFlag(SchedulingProcessMetricMask.GpuUsage))
        {
            result.Add(ProcessGpuUsage);
        }
        if (mask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory))
        {
            result.Add(ProcessGpuVram);
        }
        if (mask.HasFlag(SchedulingProcessMetricMask.RuntimeState))
        {
            result.Add(ProcessRuntimeState);
        }
        return result;
    }

    public static SchedulingProcessMetricMask ToSchedulingMetric(
        string datasetId)
    {
        if (datasetId.Equals(ProcessCpuUsage, StringComparison.OrdinalIgnoreCase))
        {
            return SchedulingProcessMetricMask.CpuUsage;
        }
        if (datasetId.Equals(ProcessMemoryUsage, StringComparison.OrdinalIgnoreCase))
        {
            return SchedulingProcessMetricMask.MemoryUsage;
        }
        if (datasetId.Equals(ProcessGpuUsage, StringComparison.OrdinalIgnoreCase))
        {
            return SchedulingProcessMetricMask.GpuUsage;
        }
        if (datasetId.Equals(ProcessGpuVram, StringComparison.OrdinalIgnoreCase))
        {
            return SchedulingProcessMetricMask.GpuDedicatedMemory;
        }
        if (datasetId.Equals(ProcessRuntimeState, StringComparison.OrdinalIgnoreCase))
        {
            return SchedulingProcessMetricMask.RuntimeState;
        }
        return SchedulingProcessMetricMask.None;
    }

    public static bool IsProcessMetricInDataset(
        string metricId,
        IReadOnlySet<string> datasetIds)
        => datasetIds.Contains(ForProcessMetric(metricId));

    public static bool IsSystemMetricInDataset(
        string metricId,
        IReadOnlySet<string> datasetIds)
        => MetricDatasetIds.IsSystemMetricInDataset(metricId, datasetIds);

    public static IReadOnlySet<string> ResolveSystemDatasets(MetricSampleRequest request, IEnumerable<string> availableDatasetIds)
        => MetricDatasetIds.ResolveSystemDatasets(request, availableDatasetIds);

    public static IReadOnlySet<string> ResolveFailedSystemDatasets(MetricSampleRequest request, IEnumerable<string> trackedDatasetIds)
        => MetricDatasetIds.ResolveFailedSystemDatasets(request, trackedDatasetIds);

    private static string Normalize(string metricId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricId);
        return metricId.Trim();
    }

    private static bool TryParseGpuMetric(
        string metricId,
        out int index,
        out string metricName)
    {
        index = 0;
        metricName = string.Empty;
        const string prefix = "gpu.";
        if (!metricId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var indexEnd = metricId.IndexOf('.', prefix.Length);
        if (indexEnd <= prefix.Length
            || !int.TryParse(metricId[prefix.Length..indexEnd], out index))
        {
            return false;
        }
        metricName = metricId[(indexEnd + 1)..];
        return metricName.Length != 0;
    }
}

