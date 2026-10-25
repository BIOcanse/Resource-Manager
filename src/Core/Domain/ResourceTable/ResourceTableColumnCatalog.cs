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

public static class ResourceTableViewModes
{
    public const string Software = "software";
    public const string Process = "process";
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

public sealed record ResourceTableColumn(string Id, string Unit, bool Visible, bool Sortable, double Width);
public sealed record ResourceTableProviderState(string Id, string State, string Message);
