using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.ResourceTable;

public sealed partial class ResourceTableProjector
{
    private static void ApplyColumnValue(
        Dictionary<string, ColumnAccumulator> columns,
        ResourceBreakdownBar bar,
        double value,
        double percent,
        double? sharedValue = null)
    {
        var columnId = ColumnIdForMetric(bar.MetricId);
        if (columnId is null)
        {
            return;
        }

        if (!columns.TryGetValue(columnId, out var accumulator))
        {
            accumulator = new ColumnAccumulator();
            columns[columnId] = accumulator;
        }

        accumulator.Add(value, percent, bar.CapacityValue ?? 0, bar.Unit);
        if (sharedValue is { } shared) accumulator.SharedValue = (accumulator.SharedValue ?? 0) + shared;
    }

    private static void MarkColumnUnavailable(
        Dictionary<string, ColumnAccumulator> columns,
        ResourceBreakdownBar bar)
    {
        var columnId = ColumnIdForMetric(bar.MetricId);
        if (columnId is null)
        {
            return;
        }

        if (!columns.TryGetValue(columnId, out var accumulator))
        {
            accumulator = new ColumnAccumulator();
            columns[columnId] = accumulator;
        }

        accumulator.MarkUnavailable(bar.Unit);
    }

    private static IReadOnlyDictionary<string, ResourceTableValue> CreateValues(
        IReadOnlyList<ResourceTableColumn> visibleColumns,
        IReadOnlyDictionary<string, ColumnAccumulator> accumulators)
    {
        var values = new Dictionary<string, ResourceTableValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in visibleColumns)
        {
            if (column.Id is ResourceTableColumnIds.Name or ResourceTableColumnIds.Status)
            {
                continue;
            }

            if (accumulators.TryGetValue(column.Id, out var accumulator)
                && accumulator.HasValue)
            {
                values[column.Id] = new ResourceTableValue(
                    accumulator.Value,
                    SanitizePercent(accumulator.Percent),
                    string.Empty,
                    accumulator.Unit,
                    null) { SharedValue = accumulator.SharedValue };
            }
            else if (accumulator is not null)
            {
                values[column.Id] = new ResourceTableValue(
                    null,
                    null,
                    string.Empty,
                    accumulator.Unit,
                    accumulator.Availability);
            }
            else
            {
                values[column.Id] = new ResourceTableValue(
                    null,
                    null,
                    string.Empty,
                    column.Unit,
                    null);
            }
        }

        return values;
    }

    private static IReadOnlyDictionary<string, ResourceTableValue> CreateProcessValues(
        IReadOnlyList<ResourceTableColumn> visibleColumns,
        IReadOnlyDictionary<string, ColumnAccumulator> accumulators,
        ResourceProcessSegment process)
    {
        var values = new Dictionary<string, ResourceTableValue>(CreateValues(visibleColumns, accumulators), StringComparer.OrdinalIgnoreCase);
        foreach (var column in visibleColumns)
        {
            if (column.Id.Equals(ResourceTableColumnIds.ProcessId, StringComparison.OrdinalIgnoreCase))
            {
                var processId = ProcessIdOrNull(process);
                values[column.Id] = new ResourceTableValue(
                    processId,
                    null,
                    processId?.ToString() ?? "--",
                    "",
                    null,
                    null,
                    process.AttributionKind);
            }
            else if (column.Id.Equals(ResourceTableColumnIds.User, StringComparison.OrdinalIgnoreCase))
            {
                var display = string.IsNullOrWhiteSpace(process.UserName) ? "--" : process.UserName;
                values[column.Id] = new ResourceTableValue(
                    null,
                    null,
                    display,
                    "",
                    display == "--" ? "Unavailable" : null,
                    null,
                    process.AttributionKind);
            }
            else if (column.Id.Equals(ResourceTableColumnIds.Architecture, StringComparison.OrdinalIgnoreCase))
            {
                var display = string.IsNullOrWhiteSpace(process.Architecture) ? "--" : process.Architecture;
                values[column.Id] = new ResourceTableValue(
                    null,
                    null,
                    display,
                    "",
                    display == "--" ? "Unavailable" : null,
                    null,
                    process.AttributionKind);
            }
        }

        return values;
    }

    private static string ProcessKey(ResourceProcessSegment process)
    {
        if (IsSystemResidual(process))
        {
            return $"{ResourceProcessAttributionKinds.SystemResidual}:{process.Name}";
        }

        return process.ProcessStartKey is > 0
            ? $"{process.ProcessId}:{process.ProcessStartKey.Value}"
            : $"{process.AttributionKind}:{process.ProcessId}:{process.Name}";
    }

    private static string ProcessStatus(ResourceProcessSegment process)
    {
        if (IsSystemResidual(process))
        {
            return SoftwareDisplayKinds.SystemResidual;
        }

        // DXGKrnl/VidMm 是 Windows 的组件名，各语言都用原名；其余进程给状态标识。
        return IsEtwResidualProcess(process) ? "DXGKrnl/VidMm" : ResourceTableProcessStates.Running;
    }

    private static int? ProcessIdOrNull(ResourceProcessSegment process)
    {
        return process.ProcessId <= 0 || IsSystemResidual(process) ? null : process.ProcessId;
    }

    private static bool IsSystemResidual(ResourceProcessSegment process)
    {
        return process.AttributionKind.Equals(ResourceProcessAttributionKinds.SystemResidual, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEtwResidualProcess(ResourceProcessSegment process)
    {
        return process.AttributionKind.Equals(ResourceProcessAttributionKinds.EtwResidualProcess, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, double> CreateSortKeys(
        IReadOnlyDictionary<string, ResourceTableValue> values)
    {
        return values.ToDictionary(
            static item => item.Key,
            static item => item.Value.Value ?? double.MinValue,
            StringComparer.OrdinalIgnoreCase);
    }

    private static double CalculateImpactScore(IReadOnlyDictionary<string, ResourceTableValue> values)
    {
        return Percent(values, ResourceTableColumnIds.Cpu) * 1.0
            + DynamicPercent(values, ".usage") * 1.0
            + DynamicPercent(values, ".vram") * 1.0
            + Percent(values, ResourceTableColumnIds.Memory) * 0.9
            + Percent(values, ResourceTableColumnIds.Disk) * 0.45
            + Percent(values, ResourceTableColumnIds.Network) * 0.25;
    }

    private static double Percent(IReadOnlyDictionary<string, ResourceTableValue> values, string columnId)
    {
        return values.TryGetValue(columnId, out var value) ? value.Percent ?? 0 : 0;
    }

    private static double DynamicPercent(IReadOnlyDictionary<string, ResourceTableValue> values, string suffix)
    {
        return values
            .Where(item => item.Key.StartsWith(ResourceBreakdownMetricIds.GpuPrefix, StringComparison.OrdinalIgnoreCase)
                && item.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Sum(static item => item.Value.Percent ?? 0);
    }

    private static string? ColumnIdForMetric(string metricId)
    {
        if (metricId.Equals(ResourceBreakdownMetricIds.CpuUsage, StringComparison.OrdinalIgnoreCase))
        {
            return ResourceTableColumnIds.Cpu;
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.MemoryUsage, StringComparison.OrdinalIgnoreCase))
        {
            return ResourceTableColumnIds.Memory;
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.DiskIo, StringComparison.OrdinalIgnoreCase))
        {
            return ResourceTableColumnIds.Disk;
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.NetworkTraffic, StringComparison.OrdinalIgnoreCase))
        {
            return ResourceTableColumnIds.Network;
        }

        if (metricId.StartsWith(ResourceBreakdownMetricIds.GpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ResourceTableColumnCatalog.IsGpuColumnId(metricId)
                ? metricId
                : null;
        }

        return null;
    }

    private static double SanitizePercent(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : Math.Min(value, 100);
    }


}
