using System.Text.Json.Serialization;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Domain.ResourceTable;

public static class ResourceTableColumnIds
{
    public const string Name = "name";
    public const string ProcessId = "pid";
    public const string Status = "status";
    public const string User = "user";
    public const string Architecture = "architecture";
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string Disk = "disk";
    public const string Network = "network";
}

public static class ResourceTableRowKinds
{
    public const string Summary = "summary";
    public const string Software = "software";
    public const string Process = "process";
}

public static class ResourceTableSortIds
{
    public const string Impact = "impact";
}

public static class ResourceTableViewModes
{
    public const string Software = "software";
    public const string Process = "process";
}

public static class ResourceTableProjectionStatuses
{
    public const string Warming = "warming";
    public const string Ready = "ready";
    public const string Stale = "stale";
    public const string Failed = "failed";
}

public static class ResourceTableColumnCatalog
{
    public static IReadOnlyList<ResourceTableColumn> KnownColumns()
    {
        return
        [
            new(ResourceTableColumnIds.Name, "名称", "", true, true, 260),
            new(ResourceTableColumnIds.ProcessId, "PID", "", true, true, 76),
            new(ResourceTableColumnIds.Status, "状态", "", true, true, 92),
            new(ResourceTableColumnIds.User, "用户", "", true, true, 150),
            new(ResourceTableColumnIds.Architecture, "架构", "", true, true, 76),
            new(ResourceTableColumnIds.Cpu, "CPU", "%", true, true, 86),
            new(ResourceTableColumnIds.Memory, "内存", "B", true, true, 110),
            new(ResourceTableColumnIds.Disk, "磁盘", "B/s", true, true, 112),
            new(ResourceTableColumnIds.Network, "网络", "bps", true, true, 106)
        ];
    }

    public static bool IsKnownColumnId(string? columnId)
    {
        if (string.IsNullOrWhiteSpace(columnId))
        {
            return false;
        }

        var normalized = columnId.Trim();
        return KnownColumns().Any(column => column.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            || IsGpuColumnId(normalized);
    }

    public static bool IsGpuColumnId(string columnId)
    {
        return TryParseGpuColumn(columnId, out _, out _);
    }

    public static bool TryParseGpuColumn(string columnId, out int index, out string metricName)
    {
        index = -1;
        metricName = "";
        var parts = columnId.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !parts[0].Equals("gpu", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[1], out index)
            || index < 0)
        {
            return false;
        }

        metricName = parts[2].ToLowerInvariant();
        return metricName is "usage" or "vram";
    }

    public static ResourceTableColumn CreateGpuColumn(string columnId, string? label = null)
    {
        return TryParseGpuColumn(columnId, out var index, out var metricName)
            ? new ResourceTableColumn(
                columnId,
                string.IsNullOrWhiteSpace(label)
                    ? metricName == "vram" ? $"GPU{index} 显存占用" : $"GPU{index} 占用率"
                    : label.Trim(),
                metricName == "vram" ? "B" : "%",
                true,
                true,
                metricName == "vram" ? 132 : 108)
            : throw new ArgumentException($"Unknown GPU column id: {columnId}", nameof(columnId));
    }

    public static IReadOnlyList<string> RequiredMetricIds(
        IEnumerable<string> columnIds)
    {
        ArgumentNullException.ThrowIfNull(columnIds);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawColumnId in columnIds)
        {
            if (string.IsNullOrWhiteSpace(rawColumnId))
            {
                continue;
            }

            var columnId = rawColumnId.Trim();
            if (columnId.Equals(ResourceTableColumnIds.Cpu, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(ResourceBreakdownMetricIds.CpuUsage);
            }
            else if (columnId.Equals(ResourceTableColumnIds.Memory, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(ResourceBreakdownMetricIds.MemoryUsage);
            }
            else if (columnId.Equals(ResourceTableColumnIds.Disk, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(ResourceBreakdownMetricIds.DiskIo);
            }
            else if (columnId.Equals(ResourceTableColumnIds.Network, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(ResourceBreakdownMetricIds.NetworkTraffic);
            }
            else if (IsGpuColumnId(columnId))
            {
                result.Add(columnId);
            }
        }

        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public sealed record ResourceTableRequest(
    IReadOnlyList<string> ColumnIds,
    string SortColumnId,
    string SortDirection,
    string ViewMode,
    IReadOnlySet<string> ExpandedSoftwareIds,
    bool IncludeProcessRows);

public sealed record ResourceTableSnapshot(
    DateTimeOffset? CapturedAt,
    string Status,
    IReadOnlyList<ResourceTableDatasetInput> InputDatasets,
    IReadOnlyList<ResourceTableColumn> Columns,
    IReadOnlyList<ResourceTableRow> Rows,
    IReadOnlyList<ResourceTableProviderState> ProviderStates,
    ResourceTableSort Sort,
    string ViewMode);

public sealed record ResourceTableDatasetInput(
    string DatasetId,
    string Status,
    ulong Generation,
    long StateRevision,
    DateTimeOffset? CapturedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? ReadyUntil,
    string? FailureCode,
    string? FailureMessage);

public sealed record ResourceTableColumn(
    string Id,
    string Label,
    string Unit,
    bool Visible,
    bool Sortable,
    double Width);

public sealed record ResourceTableRow(
    string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ParentId,
    int Depth,
    string Kind,
    string Name,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SoftwareId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ProcessId,
    int ProcessCount,
    double ImpactScore,
    IReadOnlyDictionary<string, ResourceTableValue> Values,
    [property: JsonIgnore]
    IReadOnlyDictionary<string, double> SortKeys)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoftwareName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? ProcessIds { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ProcessNames { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ExecutablePaths { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessStartKey { get; init; }
}

public sealed record ResourceTableValue(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Percent,
    string DisplayValue,
    [property: JsonIgnore]
    string Unit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Availability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? HeatPercent = null,
    [property: JsonIgnore]
    string AttributionKind = "direct")
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SharedValue { get; init; }
}

public sealed record ResourceTableProviderState(
    string Id,
    string State,
    string Message);

public sealed record ResourceTableSort(
    string ColumnId,
    string Direction);
