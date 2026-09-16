using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private static IReadOnlyList<string> NormalizeMetricIds(IEnumerable<string> metricIds)
    {
        return metricIds
            .SelectMany(SplitMetricId)
            .Select(static id => id.Trim())
            .Where(static id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(MonitoringMetricIdPriorityComparer.Instance)
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeMetricIdsInRequestOrder(IEnumerable<string> metricIds)
    {
        return metricIds
            .SelectMany(SplitMetricId)
            .Select(static id => id.Trim())
            .Where(static id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ResourceBreakdownSampleRequest? CreateResourceBreakdownSampleRequest(
        IReadOnlyList<string> itemIds,
        IReadOnlyList<NativeItemSamplingSubscriptionSourceView<ResourceBreakdownSampleRequest>> sources)
    {
        if (itemIds.Count == 0)
        {
            return null;
        }

        var dueItems = itemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevantSources = sources
            .Where(source => source.ItemIds.Any(dueItems.Contains))
            .OrderByDescending(static source => source.LastSeen)
            .ToArray();
        var metricIds = relevantSources
            .SelectMany(static source => source.Request.MetricIds)
            .SelectMany(SplitMetricId)
            .Where(metricId => SamplingDatasetIds.IsProcessMetricInDataset(
                metricId,
                dueItems))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(MonitoringMetricIdPriorityComparer.Instance)
            .ToArray();
        metricIds = ExpandProcessDatasetMetricIds(metricIds, dueItems);
        var detailLevel = relevantSources.Length == 0
            ? ProcessSampleDetailLevel.SmartSchedulingLite
            : relevantSources.Max(static source => source.Request.ProcessDetailLevel);
        var schedulingMetricMask = itemIds.Aggregate(
            SchedulingProcessMetricMask.None,
            static (mask, datasetId) =>
                mask | SamplingDatasetIds.ToSchedulingMetric(datasetId));
        var intervals = itemIds.ToDictionary(
            static datasetId => datasetId,
            datasetId => relevantSources
                .Where(source => source.ItemIds.Contains(
                    datasetId,
                    StringComparer.OrdinalIgnoreCase))
                .Select(static source => source.Interval)
                .DefaultIfEmpty(TimeSpan.FromSeconds(1))
                .Min(),
            StringComparer.OrdinalIgnoreCase);
        return new ResourceBreakdownSampleRequest(
            metricIds,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            detailLevel,
            schedulingMetricMask)
        {
            PublicationDatasetIds = itemIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            DatasetRefreshIntervals = intervals
        };
    }

    internal static IEnumerable<string> GetSubscriptionDatasetIds(
        ResourceBreakdownSampleRequest request)
    {
        var result = GetPublicationDatasetIds(request)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (result.Count > 0)
        {
            result.Add(SamplingDatasetIds.ProcessInventory);
            result.Add(SamplingDatasetIds.ProcessAttribution);
        }
        return result;
    }

    internal static IEnumerable<string> GetPublicationDatasetIds(
        ResourceBreakdownSampleRequest request)
    {
        var result = request.MetricIds
            .SelectMany(SplitMetricId)
            .Select(SamplingDatasetIds.ForProcessMetric)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.UnionWith(
            SamplingDatasetIds.ForSchedulingMetrics(
                request.SchedulingMetricMask));
        return result;
    }

    internal static bool RequiresProcessAttribution(
        ResourceBreakdownSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.MetricIds.Any(IsSupportedMetric)
            || request.PublicationDatasetIds.Contains(
                SamplingDatasetIds.ProcessAttribution,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string[] ExpandProcessDatasetMetricIds(
        IReadOnlyList<string> metricIds,
        IReadOnlySet<string> dueDatasets)
    {
        var result = metricIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (dueDatasets.Contains(SamplingDatasetIds.ProcessDiskThroughput))
        {
            result.Add(ResourceBreakdownMetricIds.DiskIo);
            result.Add(ResourceBreakdownMetricIds.DiskRead);
            result.Add(ResourceBreakdownMetricIds.DiskWrite);
        }
        if (dueDatasets.Contains(SamplingDatasetIds.ProcessNetworkThroughput))
        {
            result.Add(ResourceBreakdownMetricIds.NetworkTraffic);
            result.Add(ResourceBreakdownMetricIds.NetworkReceive);
            result.Add(ResourceBreakdownMetricIds.NetworkSend);
        }
        if (dueDatasets.Contains(SamplingDatasetIds.ProcessRawNetworkThroughput))
        {
            result.Add(ResourceBreakdownMetricIds.NetworkRawTraffic);
            result.Add(ResourceBreakdownMetricIds.NetworkRawReceive);
            result.Add(ResourceBreakdownMetricIds.NetworkRawSend);
        }
        return result
            .Order(MonitoringMetricIdPriorityComparer.Instance)
            .ToArray();
    }

    private static bool IsSupportedMetric(string metricId)
    {
        return metricId.Equals("cpu.usage", StringComparison.OrdinalIgnoreCase)
            || metricId.Equals("memory.usage", StringComparison.OrdinalIgnoreCase)
            || metricId.Equals("virtualMemory.usage", StringComparison.OrdinalIgnoreCase)
            || IsDiskMetric(metricId)
            || IsNetworkMetric(metricId)
            || (TryParseGpuMetric(metricId, out _, out var metricName)
                && (metricName.Equals("usage", StringComparison.OrdinalIgnoreCase)
                    || metricName.Equals("vram", StringComparison.OrdinalIgnoreCase)));
    }

    private static ResourceBreakdownSampleRequest SelectSubscribable(
        ResourceBreakdownSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var metricIds = NormalizeMetricIdsInRequestOrder(request.MetricIds)
            .Where(IsSupportedMetric)
            .ToArray();
        var selected = metricIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scaleModes = request.ScaleModes
            .Where(pair => selected.Contains(pair.Key))
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        return new ResourceBreakdownSampleRequest(
            metricIds,
            scaleModes,
            request.ProcessDetailLevel,
            request.SchedulingMetricMask)
        {
            PublicationDatasetIds = request.PublicationDatasetIds,
            DatasetRefreshIntervals = request.DatasetRefreshIntervals
        };
    }

    private static bool IsDiskMetric(string metricId)
    {
        return metricId.Equals(ResourceBreakdownMetricIds.DiskIo, StringComparison.OrdinalIgnoreCase)
            || metricId.Equals(ResourceBreakdownMetricIds.DiskRead, StringComparison.OrdinalIgnoreCase)
            || metricId.Equals(ResourceBreakdownMetricIds.DiskWrite, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNetworkMetric(string metricId)
    {
        return metricId.Equals(ResourceBreakdownMetricIds.NetworkTraffic, StringComparison.OrdinalIgnoreCase)
            || metricId.Equals(ResourceBreakdownMetricIds.NetworkReceive, StringComparison.OrdinalIgnoreCase)
            || metricId.Equals(ResourceBreakdownMetricIds.NetworkSend, StringComparison.OrdinalIgnoreCase)
            || IsRawNetworkMetric(metricId);
    }

    private static bool IsRawNetworkMetric(string metricId)
    {
        return metricId.Equals(ResourceBreakdownMetricIds.NetworkRawTraffic, StringComparison.OrdinalIgnoreCase)
            || metricId.Equals(ResourceBreakdownMetricIds.NetworkRawReceive, StringComparison.OrdinalIgnoreCase)
            || metricId.Equals(ResourceBreakdownMetricIds.NetworkRawSend, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ExpandHardwareMetricIds(IReadOnlyList<string> metricIds)
    {
        var expanded = metricIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (metricIds.Any(IsNetworkMetric))
        {
            expanded.Add("network.total.receiveBytesPerSec");
            expanded.Add("network.total.sendBytesPerSec");
        }

        return expanded.ToArray();
    }

    private static MetricSampleRequest CreateHardwareRequest(
        IReadOnlyList<string> metricIds,
        SchedulingProcessMetricMask schedulingMetricMask)
    {
        var expanded = ExpandHardwareMetricIds(metricIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (schedulingMetricMask.HasFlag(
                SchedulingProcessMetricMask.MemoryUsage))
        {
            expanded.Add("memory.usage");
            expanded.Add("memory.percent");
        }
        var needsGpuInventory = schedulingMetricMask.HasFlag(
                SchedulingProcessMetricMask.GpuUsage)
            || schedulingMetricMask.HasFlag(
                SchedulingProcessMetricMask.GpuDedicatedMemory);
        return needsGpuInventory
            ? MetricSampleRequest.ForIdsAndAllGpuCoreMetrics(expanded)
            : MetricSampleRequest.ForIds(expanded);
    }

    private static MetricSampleRequest CreateHardwareRequest(
        ResourceBreakdownSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateHardwareRequest(
            request.MetricIds,
            request.SchedulingMetricMask);
    }

    private static NetworkAttributionReadRequest CreateNetworkAttributionRequest(
        HardwareMetricSnapshot snapshot,
        IReadOnlyList<ProcessResourceSample> processSamples)
    {
        var processNames =
            new Dictionary<int, string>(processSamples.Count);
        foreach (var sample in processSamples)
        {
            if (sample.ProcessId > 0
                && !string.IsNullOrWhiteSpace(sample.Name))
            {
                processNames.TryAdd(
                    sample.ProcessId,
                    sample.Name);
            }
        }

        return new NetworkAttributionReadRequest(
            snapshot.Items.GetValueOrDefault("network.total.receiveBytesPerSec")?.NumericValue ?? 0,
            snapshot.Items.GetValueOrDefault("network.total.sendBytesPerSec")?.NumericValue ?? 0,
            processNames);
    }

    private static bool IsGpuMetric(string metricId)
    {
        return TryParseGpuMetric(metricId, out _, out _);
    }

    private static bool TryParseGpuMetric(string metricId, out int index, out string metricName)
    {
        index = 0;
        metricName = string.Empty;
        const string prefix = "gpu.";
        if (!metricId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var indexStart = prefix.Length;
        var indexEnd = metricId.IndexOf('.', indexStart);
        if (indexEnd <= indexStart || !int.TryParse(metricId[indexStart..indexEnd], out index))
        {
            return false;
        }

        metricName = metricId[(indexEnd + 1)..];
        return !string.IsNullOrWhiteSpace(metricName);
    }

    private static IEnumerable<string> SplitMetricId(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static double SanitizePercent(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : Math.Min(value, 100);
    }

    private static double NormalizeTotalValue(double value, double capacityValue)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            return 0;
        }

        return capacityValue > 0 ? Math.Min(value, capacityValue) : value;
    }



    private static bool MatchesProcessName(string actualName, string declaredName)
    {
        if (string.IsNullOrWhiteSpace(actualName) || string.IsNullOrWhiteSpace(declaredName))
        {
            return false;
        }

        var declared = Path.GetFileNameWithoutExtension(declaredName.Trim());
        return actualName.Equals(declared, StringComparison.OrdinalIgnoreCase)
            || actualName.Equals(declaredName.Trim(), StringComparison.OrdinalIgnoreCase);
    }


    private readonly record struct ProcessResourceSample(
        int ProcessId,
        int? ParentProcessId,
        long? StartKey,
        string Name,
        string? ExecutablePath,
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        TimeSpan TotalProcessorTime,
        bool HasWorkingSetBytes,
        bool HasPrivateMemoryBytes,
        bool HasProcessorTime,
        double CpuPercent,
        bool HasCpuPercent,
        bool IsSelfDescendant,
        string? FileDescription,
        string? ProductName,
        string? CompanyName,
        string? ApplicationUserModelId,
        string? WindowApplicationUserModelId,
        string? WindowTitle,
        string? UserName,
        string? Architecture);

    private readonly record struct ProcessSampleBatch(
        SamplingObservationStatus Status,
        uint EnumeratedCount,
        uint ExcludedCount,
        uint SkippedCount,
        IReadOnlyList<ProcessResourceSample> Samples);

    internal readonly record struct SchedulingProcessSampleAccounting(
        SamplingObservationStatus Status,
        uint EnumeratedCount,
        uint ExcludedCount,
        uint SkippedCount,
        uint SampleCount,
        uint GovernableIdentityCount);

    private sealed record ProcessFileMetadata(
        string? FileDescription,
        string? ProductName,
        string? CompanyName)
    {
        public static ProcessFileMetadata Empty { get; } = new(null, null, null);
    }

    private sealed record CachedProcessIdentity(
        string ProcessName,
        string? ExecutablePath,
        ProcessFileMetadata Metadata,
        string? ApplicationUserModelId,
        string? WindowApplicationUserModelId,
        string? WindowTitle,
        string? UserName,
        string? Architecture,
        DateTimeOffset ObservedAt);

    private readonly record struct AttributedProcess(
        RuntimeSoftwareAttribution Software,
        ProcessResourceSample Process,
        double Value,
        double SystemPercent,
        double BaseScore);

    private static string? CleanMetadata(string? value)
    {
        var clean = value?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }
}
