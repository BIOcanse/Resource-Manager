using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;

namespace ResourceManager.App.Infrastructure.ResourceTable;

public sealed partial class ResourceTableProjector
{
    private static RowAccumulator GetSoftwareAccumulator(
        Dictionary<string, RowAccumulator> accumulators,
        ResourceSoftwareSegment segment)
    {
        if (accumulators.TryGetValue(segment.SoftwareId, out var accumulator))
        {
            return accumulator;
        }

        accumulator = new RowAccumulator(segment);
        accumulators[segment.SoftwareId] = accumulator;
        return accumulator;
    }

    private static ResourceTableRow CreateSummaryRow(
        ResourceBreakdownSnapshot breakdown,
        IReadOnlyList<ResourceTableColumn> visibleColumns,
        string projectionStatus)
    {
        var visibleColumnIds = visibleColumns
            .Select(static column => column.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = new Dictionary<string, ColumnAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var bar in breakdown.Bars)
        {
            var columnId = ColumnIdForMetric(bar.MetricId);
            if (columnId is null || !visibleColumnIds.Contains(columnId))
            {
                continue;
            }

            if (bar.TotalValue is double totalValue
                && bar.TotalSystemPercent is double totalSystemPercent
                && bar.CapacityValue is not null)
            {
                ApplyColumnValue(
                    columns,
                    bar,
                    totalValue,
                    totalSystemPercent,
                    bar.TotalDisplay,
                    bar.SharedValue);
            }
            else
            {
                MarkColumnUnavailable(columns, bar);
            }
        }

        var values = CreateValues(visibleColumns, columns);
        var sortKeys = CreateSortKeys(values);
        return new ResourceTableRow(
            "summary:total",
            null,
            0,
            ResourceTableRowKinds.Summary,
            "总占用",
            projectionStatus == ResourceTableProjectionStatuses.Ready
                ? "当前采样"
                : "-",
            null,
            null,
            0,
            CalculateImpactScore(values),
            values,
            sortKeys);
    }

    private sealed class RowAccumulator
    {
        private readonly ResourceSoftwareSegment software;
        private readonly Dictionary<string, ColumnAccumulator> columns = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ProcessAccumulator> processes = new(StringComparer.OrdinalIgnoreCase);
        private int processCount;

        public RowAccumulator(ResourceSoftwareSegment software)
        {
            this.software = software;
        }

        public void ApplySoftwareValue(ResourceBreakdownBar bar, ResourceSoftwareSegment segment)
        {
            processCount = Math.Max(processCount, segment.ProcessCount);
            ApplyColumnValue(columns, bar, segment.Value, segment.SystemPercent, segment.DisplayValue, segment.SharedValue);
        }

        public void ApplyProcessValue(ResourceBreakdownBar bar, ResourceProcessSegment process)
        {
            var processKey = ProcessKey(process);
            if (!processes.TryGetValue(processKey, out var accumulator))
            {
                accumulator = new ProcessAccumulator(processKey, process);
                processes[processKey] = accumulator;
            }

            accumulator.Apply(bar, process);
        }

        public ResourceTableRow ToSoftwareRow(
            IReadOnlyList<ResourceTableColumn> visibleColumns,
            ResourceTableRequest request)
        {
            var values = CreateValues(visibleColumns, columns);
            var sortKeys = CreateSortKeys(values);
            var impact = CalculateImpactScore(values);
            var includeProcessMetadata = request.IncludeProcessRows
                || request.ExpandedSoftwareIds.Contains(software.SoftwareId);
            var row = new ResourceTableRow(
                $"software:{software.SoftwareId}",
                null,
                0,
                ResourceTableRowKinds.Software,
                software.Name,
                software.DisplayKind,
                software.SoftwareId,
                null,
                processCount,
                impact,
                values,
                sortKeys);

            return includeProcessMetadata
                ? row with
                {
                    ProcessIds = processes.Values.Select(static process => process.ProcessId).Where(static processId => processId is not null).Select(static processId => processId!.Value).Distinct().ToArray(),
                    ProcessNames = processes.Values.Select(static process => process.Name).Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    ExecutablePaths = processes.Values.Select(static process => process.ExecutablePath).Where(static path => !string.IsNullOrWhiteSpace(path)).Select(static path => path!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                }
                : row;
        }

        public IReadOnlyList<ResourceTableRow> ToProcessRows(
            IReadOnlyList<ResourceTableColumn> visibleColumns,
            string parentId,
            ResourceTableRequest request)
        {
            var columnId = NormalizeSortColumn(request.SortColumnId);
            var direction = NormalizeSortDirection(request.SortDirection);
            var rows = processes.Values
                .Select(process => process.ToGroupedRow(visibleColumns, parentId, software.SoftwareId, software.Name))
                .ToArray();
            return direction.Equals("asc", StringComparison.OrdinalIgnoreCase)
                ? rows.OrderBy(row => SortKey(row, columnId)).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
                : rows.OrderByDescending(row => SortKey(row, columnId)).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        public IReadOnlyList<ResourceTableRow> ToFlatProcessRows(
            IReadOnlyList<ResourceTableColumn> visibleColumns,
            ResourceTableRequest request)
        {
            return processes.Values
                .Select(process => process.ToFlatRow(visibleColumns, software))
                .ToArray();
        }
    }

    private sealed class ProcessAccumulator
    {
        private readonly string processKey;
        private readonly ResourceProcessSegment process;
        private readonly Dictionary<string, ColumnAccumulator> columns = new(StringComparer.OrdinalIgnoreCase);

        public ProcessAccumulator(string processKey, ResourceProcessSegment process)
        {
            this.processKey = processKey;
            this.process = process;
        }

        public void Apply(ResourceBreakdownBar bar, ResourceProcessSegment processValue)
        {
            ApplyColumnValue(columns, bar, processValue.Value, processValue.SystemPercent, processValue.DisplayValue, processValue.SharedValue);
        }

        public ResourceTableRow ToGroupedRow(
            IReadOnlyList<ResourceTableColumn> visibleColumns,
            string parentId,
            string softwareId,
            string softwareName)
        {
            var values = CreateProcessValues(visibleColumns, columns, process);
            var sortKeys = CreateSortKeys(values);
            return new ResourceTableRow(
                $"process:{softwareId}:{processKey}",
                parentId,
                1,
                ResourceTableRowKinds.Process,
                process.Name,
                ProcessStatus(process),
                softwareId,
                ProcessIdOrNull(process),
                0,
                CalculateImpactScore(values),
                values,
                sortKeys)
            {
                SoftwareName = softwareName,
                ProcessStartKey = ProcessStartKeyOrNull(process),
                ProcessIds = ProcessIdOrNull(process) is { } processId ? [processId] : [],
                ProcessNames = string.IsNullOrWhiteSpace(process.Name) ? [] : [process.Name],
                ExecutablePaths = string.IsNullOrWhiteSpace(process.ExecutablePath) ? [] : [process.ExecutablePath]
            };
        }

        public ResourceTableRow ToFlatRow(
            IReadOnlyList<ResourceTableColumn> visibleColumns,
            ResourceSoftwareSegment software)
        {
            var values = CreateProcessValues(visibleColumns, columns, process);
            var sortKeys = CreateSortKeys(values);
            return new ResourceTableRow(
                $"process:{software.SoftwareId}:{processKey}",
                null,
                0,
                ResourceTableRowKinds.Process,
                process.Name,
                ProcessStatus(process),
                software.SoftwareId,
                ProcessIdOrNull(process),
                0,
                CalculateImpactScore(values),
                values,
                sortKeys)
            {
                SoftwareName = software.Name,
                ProcessStartKey = ProcessStartKeyOrNull(process),
                ProcessIds = ProcessIdOrNull(process) is { } processId ? [processId] : [],
                ProcessNames = string.IsNullOrWhiteSpace(process.Name) ? [] : [process.Name],
                ExecutablePaths = string.IsNullOrWhiteSpace(process.ExecutablePath) ? [] : [process.ExecutablePath]
            };
        }

        public int? ProcessId => ProcessIdOrNull(process);

        public string Name => process.Name;

        public string? ExecutablePath => string.IsNullOrWhiteSpace(process.ExecutablePath) ? null : process.ExecutablePath;

        private static string? ProcessStartKeyOrNull(ResourceProcessSegment process)
            => process.ProcessStartKey is > 0
                ? process.ProcessStartKey.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                : null;
    }

    private sealed class ColumnAccumulator
    {
        public bool HasValue { get; private set; }
        public double Value { get; set; }
        public double? SharedValue { get; set; }
        public double Percent { get; set; }
        public double Capacity { get; set; }
        public string Unit { get; set; } = "";
        public string DisplayValue { get; set; } = "--";
        public string? Availability { get; private set; }

        public void Add(double value, double percent, double capacity, string unit)
        {
            HasValue = true;
            Availability = null;
            Unit = unit;
            if (unit == "%")
            {
                Value = Math.Max(Value, value);
                Percent = Math.Max(Percent, percent);
                DisplayValue = $"{Value:0.0}%";
                return;
            }

            Value += Math.Max(0, value);
            Capacity += Math.Max(0, capacity);
            Percent = Capacity > 0 ? Value * 100 / Capacity : Math.Max(Percent, percent);
            DisplayValue = Unit switch
            {
                "B" => FormatBytes(Value),
                "B/s" => $"{FormatBytes(Value)}/s",
                _ => Value.ToString("0.0")
            };
        }

        public void MarkUnavailable(string unit)
        {
            if (HasValue)
            {
                return;
            }

            Unit = unit;
            Availability = "Unavailable";
            DisplayValue = "N/A";
        }
    }

}
