namespace ResourceManager.App.Domain.Metrics;

public sealed class MetricSampleRequest
{
    private readonly HashSet<string>? metricIds;
    private readonly bool includesCatalog;
    private readonly bool includesAllGpuCoreMetrics;

    private MetricSampleRequest(
        HashSet<string>? metricIds,
        bool includesCatalog = false,
        bool includesAllGpuCoreMetrics = false)
    {
        this.metricIds = metricIds;
        this.includesCatalog = includesCatalog;
        this.includesAllGpuCoreMetrics = includesAllGpuCoreMetrics;
    }

    public static MetricSampleRequest All { get; } = new(null);

    public static MetricSampleRequest CatalogProbe { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), includesCatalog: true);

    public bool IsAll => metricIds is null;

    public bool IsCatalogProbe => includesCatalog && metricIds is { Count: 0 } && !includesAllGpuCoreMetrics;

    public bool IncludesCatalog => IsAll || includesCatalog;

    public bool IsEmpty => metricIds is { Count: 0 } && !includesAllGpuCoreMetrics && !includesCatalog;

    public bool IncludesAllGpuCoreMetrics => IsAll || includesAllGpuCoreMetrics;

    public IReadOnlyCollection<string> Ids => metricIds ?? [];

    public string CacheKey
    {
        get
        {
            if (IsAll)
            {
                return "*";
            }

            if (IsCatalogProbe)
            {
                return "catalog";
            }

            var prefix = (includesCatalog ? "catalog\n" : string.Empty)
                + (includesAllGpuCoreMetrics ? "gpu-core:*\n" : string.Empty);
            return prefix + string.Join('\n', metricIds!.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase));
        }
    }

    public static MetricSampleRequest ForIds(IEnumerable<string> ids)
    {
        var normalized = ids
            .SelectMany(SplitMetricId)
            .Select(static id => id.Trim())
            .Where(static id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new MetricSampleRequest(normalized);
    }

    public static MetricSampleRequest ForIdsAndAllGpuCoreMetrics(IEnumerable<string> ids)
    {
        var normalized = ids
            .SelectMany(SplitMetricId)
            .Select(static id => id.Trim())
            .Where(static id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new MetricSampleRequest(normalized, includesAllGpuCoreMetrics: true);
    }

    public static MetricSampleRequest Merge(IEnumerable<MetricSampleRequest> requests)
    {
        var list = requests.ToArray();
        if (list.Length == 0)
        {
            return ForIds([]);
        }

        if (list.Any(static request => request.IsAll))
        {
            return All;
        }

        var ids = list.SelectMany(static request => request.Ids);
        var merged = list.Any(static request => request.includesAllGpuCoreMetrics)
            ? ForIdsAndAllGpuCoreMetrics(ids)
            : ForIds(ids);
        return list.Any(static request => request.IncludesCatalog) ? merged.WithCatalog() : merged;
    }

    public MetricSampleRequest WithCatalog()
        => IncludesCatalog ? this : new(metricIds, includesCatalog: true,
            includesAllGpuCoreMetrics: includesAllGpuCoreMetrics);

    public bool Includes(string metricId)
    {
        return IsAll || metricIds!.Contains(metricId)
            || includesAllGpuCoreMetrics && IsGpuCoreMetric(metricId);
    }

    public bool IncludesAny(params string[] ids)
    {
        return IsAll || ids.Any(metricIds!.Contains);
    }

    public bool IncludesPrefix(string prefix)
    {
        return IsAll
            || includesAllGpuCoreMetrics && prefix.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase)
            || metricIds!.Any(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public bool IncludesAny(Func<string, bool> predicate)
    {
        return IsAll || metricIds!.Any(predicate);
    }

    public int[] GetGpuIndexes()
    {
        if (IsAll)
        {
            return [];
        }

        return metricIds!
            .Select(ParseGpuIndex)
            .Where(static index => index is not null)
            .Select(static index => index!.Value)
            .Distinct()
            .Order()
            .ToArray();
    }

    public bool IncludesGpuMetric(int index)
    {
        return IsAll || IncludesPrefix($"gpu.{index}.");
    }

    public bool IncludesGpuMetric(int index, string metricName)
    {
        return Includes($"gpu.{index}.{metricName}");
    }

    private static IEnumerable<string> SplitMetricId(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int? ParseGpuIndex(string metricId)
    {
        const string prefix = "gpu.";
        if (!metricId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var start = prefix.Length;
        var end = metricId.IndexOf('.', start);
        return end > start && int.TryParse(metricId[start..end], out var index) ? index : null;
    }

    private static bool IsGpuCoreMetric(string metricId)
    {
        const string prefix = "gpu.";
        if (!metricId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separator = metricId.IndexOf('.', prefix.Length);
        if (separator <= prefix.Length)
        {
            return false;
        }
        var name = metricId[(separator + 1)..];
        return name.Equals("usage", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vram", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vramPercent", StringComparison.OrdinalIgnoreCase)
            || name.Equals("engineBusy", StringComparison.OrdinalIgnoreCase);
    }
}
