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

/// <summary>进程行的状态取值。只有状态，没有措辞。</summary>
public static class ResourceTableProcessStates
{
    public const string Running = "running";
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
            new(ResourceTableColumnIds.Name, "", true, true, 260),
            new(ResourceTableColumnIds.ProcessId, "", true, true, 76),
            new(ResourceTableColumnIds.Status, "", true, true, 92),
            new(ResourceTableColumnIds.User, "", true, true, 150),
            new(ResourceTableColumnIds.Architecture, "", true, true, 76),
            new(ResourceTableColumnIds.Cpu, "%", true, true, 86),
            new(ResourceTableColumnIds.Memory, "B", true, true, 110),
            new(ResourceTableColumnIds.Disk, "B/s", true, true, 112),
            new(ResourceTableColumnIds.Network, "bps", true, true, 106)
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

    public static ResourceTableColumn CreateGpuColumn(string columnId)
    {
        return TryParseGpuColumn(columnId, out _, out var metricName)
            ? new ResourceTableColumn(
                columnId,
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
    /// <summary>列的标识；表头措辞由前端按 id 出，后端不发措辞。</summary>
    string Id,
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
    // 文本单元（进程号、用户、架构）用这里的文本；数值单元留空，
    // 由前端按 Unit 和 Value 自己格式化。
    string DisplayValue,
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
