using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Domain.Monitoring;

public static class MetricDatasetIds
{
    public const string SystemMetricCatalog = "system.metric.catalog";
    public const string StorageInventory = "storage.inventory";
    public const string FanInventory = "fan.inventory";
    public const string BatteryInventory = "battery.inventory";
    public const string NumaInventory = "numa.inventory";
    public const string SystemGpuInventory = "system.gpu.inventory";
    public const string SystemCpuUsage = "cpu.usage";
    public const string SystemMemoryUsage = "memory.usage";
    public const string SystemVirtualMemoryUsage = "virtualMemory.usage";

    public static string ForSystemMetric(string metricId) => Normalize(metricId);

    public static bool IsSystemMetricInDataset(string metricId, IReadOnlySet<string> datasetIds)
        => datasetIds.Contains(ForSystemMetric(metricId));

    public static IReadOnlySet<string> ResolveAllSystemDatasets(
        IEnumerable<string> availableMetricIds)
    {
        ArgumentNullException.ThrowIfNull(availableMetricIds);
        var result = availableMetricIds
            .Select(ForSystemMetric)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.Add(SystemGpuInventory);
        return result;
    }

    public static IReadOnlySet<string> ResolveSystemDatasets(
        MetricSampleRequest request,
        IEnumerable<string> availableDatasetIds)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(availableDatasetIds);
        var available = availableDatasetIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        if (request.IsAll)
        {
            return available;
        }

        return ResolveExplicitSystemDatasets(request, available);
    }

    public static IReadOnlySet<string> ResolveFailedSystemDatasets(
        MetricSampleRequest request,
        IEnumerable<string> trackedDatasetIds)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(trackedDatasetIds);
        var tracked = trackedDatasetIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        if (request.IsAll)
        {
            return tracked;
        }

        return ResolveExplicitSystemDatasets(request, tracked);
    }

    /// <summary>Expand only explicit addresses of the same bound field.
    /// Original requests remain the seed; expansion is never transitive.</summary>
    public static HashSet<string> ExpandBoundSystemDatasets(
        IEnumerable<string> requestedDatasetIds,
        IEnumerable<MetricDeviceFieldBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(requestedDatasetIds);
        ArgumentNullException.ThrowIfNull(bindings);
        var requested = requestedDatasetIds.Select(ForSystemMetric)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        var internalSampleAddresses = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            var canonical = ForSystemMetric(binding.RequestAddress);
            var sampleAddress = ForSystemMetric(binding.EffectiveSampleRequestAddress);
            if (!requested.Contains(canonical)
                && !requested.Contains(sampleAddress)
                && !binding.RequestAliases.Any(alias => requested.Contains(ForSystemMetric(alias))))
                continue;
            result.Add(canonical);
            foreach (var alias in binding.RequestAliases)
                result.Add(ForSystemMetric(alias));
            if (binding.PdhGpuEngineIdentity is not null)
            {
                result.Add(SystemGpuInventory);
                if (!string.Equals(canonical, sampleAddress,
                    StringComparison.OrdinalIgnoreCase))
                {
                    internalSampleAddresses.Add(sampleAddress);
                }
            }
        }
        // A selected engine field expands the adapter-scoped planner trigger
        // into its exact typed datasets. The trigger remains in the source
        // query receipt, but must not become a public/current dataset seed.
        result.ExceptWith(internalSampleAddresses);
        return result;
    }

    private static IReadOnlySet<string> ResolveExplicitSystemDatasets(
        MetricSampleRequest request,
        IReadOnlySet<string> availableDatasetIds)
    {
        var result = request.Ids
            .Select(ForSystemMetric)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.IncludesCatalog)
        {
            result.Add(SystemMetricCatalog);
        }
        if (request.IncludesAllGpuCoreMetrics)
        {
            result.UnionWith(availableDatasetIds.Where(IsGpuCoreMetric));
        }
        if (result.Any(IsGpuMetric)
            || request.IncludesPrefix("gpu."))
        {
            result.Add(SystemGpuInventory);
        }
        return result;
    }

    private static bool IsGpuMetric(string datasetId)
        => datasetId.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase);

    private static bool IsGpuCoreMetric(string datasetId)
    {
        if (!TryParseGpuMetric(datasetId, out _, out var metricName))
        {
            return false;
        }

        return metricName.Equals("usage", StringComparison.OrdinalIgnoreCase)
            || metricName.Equals("vram", StringComparison.OrdinalIgnoreCase)
            || metricName.Equals("vramPercent", StringComparison.OrdinalIgnoreCase)
            || metricName.Equals("engineBusy", StringComparison.OrdinalIgnoreCase);
    }

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
