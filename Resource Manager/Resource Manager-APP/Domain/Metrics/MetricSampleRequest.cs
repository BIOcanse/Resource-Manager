namespace ResourceManager.App.Domain.Metrics;

public sealed class MetricSampleRequest
{
    private readonly HashSet<string>? metricIds;
    private readonly bool isCatalogProbe;
    private readonly bool includesAllGpuCoreMetrics;

    private MetricSampleRequest(
        HashSet<string>? metricIds,
        bool isCatalogProbe = false,
        bool includesAllGpuCoreMetrics = false)
    {
        this.metricIds = metricIds;
        this.isCatalogProbe = isCatalogProbe;
        this.includesAllGpuCoreMetrics = includesAllGpuCoreMetrics;
    }

    public static MetricSampleRequest All { get; } = new(null);

    public static MetricSampleRequest CatalogProbe { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), isCatalogProbe: true);

    public bool IsAll => metricIds is null;

    public bool IsCatalogProbe => isCatalogProbe;

    public bool IsEmpty => metricIds is { Count: 0 } && !includesAllGpuCoreMetrics;

    public bool IncludesAllGpuCoreMetrics => IsAll || IsCatalogProbe || includesAllGpuCoreMetrics;

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

            var prefix = includesAllGpuCoreMetrics ? "gpu-core:*\n" : string.Empty;
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
            return CatalogProbe;
        }

        if (list.Any(static request => request.IsAll || request.IsCatalogProbe))
        {
            return All;
        }

        var ids = list.SelectMany(static request => request.Ids);
        return list.Any(static request => request.includesAllGpuCoreMetrics)
            ? ForIdsAndAllGpuCoreMetrics(ids)
            : ForIds(ids);
    }

    public bool Includes(string metricId)
    {
        return IsAll || IsCatalogProbe || metricIds!.Contains(metricId)
            || includesAllGpuCoreMetrics && IsGpuCoreMetric(metricId);
    }

    public bool IncludesAny(params string[] ids)
    {
        return IsAll || IsCatalogProbe || ids.Any(metricIds!.Contains);
    }

    public bool IncludesPrefix(string prefix)
    {
        return IsAll || IsCatalogProbe
            || includesAllGpuCoreMetrics && prefix.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase)
            || metricIds!.Any(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public bool IncludesAny(Func<string, bool> predicate)
    {
        return IsAll || IsCatalogProbe || metricIds!.Any(predicate);
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
            || name.Equals("vramPercent", StringComparison.OrdinalIgnoreCase);
    }
}
