namespace ResourceManager.App.Domain.ResourceBreakdown;

public enum ProcessSampleDetailLevel : byte
{
    SmartSchedulingLite = 0,
    ResourceTableBasic = 1,
    ResourceTableFull = 2,
    DiagnosticsFull = 3
}

public sealed record ResourceBreakdownSampleRequest(
    IReadOnlyList<string> MetricIds,
    IReadOnlyDictionary<string, string> ScaleModes,
    ProcessSampleDetailLevel ProcessDetailLevel,
    SchedulingProcessMetricMask SchedulingMetricMask =
        SchedulingProcessMetricMask.None)
{
    public IReadOnlyList<string> PublicationDatasetIds { get; init; } = [];

    public IReadOnlyDictionary<string, TimeSpan> DatasetRefreshIntervals { get; init; }
        = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

    public string CacheKey
    {
        get
        {
            var metrics = MetricIds
                .SelectMany(SplitMetricId)
                .Select(static id => id.Trim())
                .Where(static id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase);
            var scaleModes = ScaleModes
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => $"{pair.Key}={pair.Value}");
            var publications = PublicationDatasetIds
                .Select(static id => id.Trim())
                .Where(static id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase);
            return $"{(byte)ProcessDetailLevel}:{(ulong)SchedulingMetricMask}:{string.Join(',', metrics)}:{string.Join(',', scaleModes)}:{string.Join(',', publications)}";
        }
    }

    public static ResourceBreakdownSampleRequest ForResourceTable(
        IReadOnlyList<string> metricIds,
        IReadOnlyDictionary<string, string> scaleModes)
    {
        return new ResourceBreakdownSampleRequest(
            metricIds,
            scaleModes,
            ProcessSampleDetailLevel.ResourceTableBasic);
    }

    public static ResourceBreakdownSampleRequest ForSmartScheduling(
        IReadOnlyList<string> metricIds,
        IReadOnlyDictionary<string, string> scaleModes)
    {
        return new ResourceBreakdownSampleRequest(
            metricIds,
            scaleModes,
            ProcessSampleDetailLevel.SmartSchedulingLite);
    }

    public static ResourceBreakdownSampleRequest Merge(IReadOnlyList<ResourceBreakdownSampleRequest> requests)
    {
        if (requests.Count == 0)
        {
            return new ResourceBreakdownSampleRequest([], new Dictionary<string, string>(), ProcessSampleDetailLevel.SmartSchedulingLite);
        }

        var metricIds = requests
            .SelectMany(static request => request.MetricIds)
            .SelectMany(SplitMetricId)
            .Select(static id => id.Trim())
            .Where(static id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var scaleModes = requests
            .SelectMany(static request => request.ScaleModes)
            .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Last().Value,
                StringComparer.OrdinalIgnoreCase);
        var detailLevel = requests.Max(static request => request.ProcessDetailLevel);

        var schedulingMetricMask = requests.Aggregate(
            SchedulingProcessMetricMask.None,
            static (mask, request) => mask | request.SchedulingMetricMask);
        var publicationDatasetIds = requests
            .SelectMany(static request => request.PublicationDatasetIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var datasetRefreshIntervals = requests
            .SelectMany(static request => request.DatasetRefreshIntervals)
            .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Min(static pair => pair.Value),
                StringComparer.OrdinalIgnoreCase);
        return new ResourceBreakdownSampleRequest(
            metricIds,
            scaleModes,
            detailLevel,
            schedulingMetricMask)
        {
            PublicationDatasetIds = publicationDatasetIds,
            DatasetRefreshIntervals = datasetRefreshIntervals
        };
    }

    private static IEnumerable<string> SplitMetricId(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
