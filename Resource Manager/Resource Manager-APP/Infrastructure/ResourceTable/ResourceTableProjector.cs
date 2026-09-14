using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;

namespace ResourceManager.App.Infrastructure.ResourceTable;

public sealed partial class ResourceTableProjector(IEnumerable<IResourceTableProviderStateSource>? providerStateSources = null) : IResourceTableProjector
{
    private readonly IReadOnlyList<IResourceTableProviderStateSource> providerStateSources =
        providerStateSources?.ToArray() ?? [];

    public ResourceTableSnapshot Project(
        ResourceBreakdownSnapshot breakdown,
        ResourceTableRequest request)
    {
        var columns = CreateColumns(request.ColumnIds, breakdown, request.ViewMode);
        var inputs = CreateInputDatasets(breakdown, columns);
        var status = ProjectionStatus(inputs);
        var sortedRows = BuildRows(breakdown, columns, request, status);
        return new ResourceTableSnapshot(
            inputs
                .Where(static input => input.CapturedAt is not null)
                .Select(static input => input.CapturedAt)
                .DefaultIfEmpty(null)
                .Max(),
            status,
            inputs,
            columns,
            sortedRows,
            ProviderStatesFor(columns),
            new ResourceTableSort(NormalizeSortColumn(request.SortColumnId), NormalizeSortDirection(request.SortDirection)),
            NormalizeViewMode(request.ViewMode));
    }

    private static IReadOnlyList<ResourceTableDatasetInput> CreateInputDatasets(
        ResourceBreakdownSnapshot breakdown,
        IReadOnlyList<ResourceTableColumn> columns)
    {
        var available = breakdown.Datasets.ToDictionary(
            static dataset => dataset.DatasetId,
            StringComparer.OrdinalIgnoreCase);
        return ResourceTableColumnCatalog.RequiredMetricIds(
                columns.Select(static column => column.Id))
            .Select(SamplingDatasetIds.ForProcessMetric)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(datasetId => available.TryGetValue(datasetId, out var state)
                ? new ResourceTableDatasetInput(
                    state.DatasetId,
                    state.Status,
                    state.Generation,
                    state.StateRevision,
                    state.CapturedAt,
                    state.LastAttemptAt,
                    state.LastSuccessAt,
                    state.ReadyUntil,
                    state.FailureCode,
                    state.FailureMessage)
                : new ResourceTableDatasetInput(
                    datasetId,
                    ResourceTableProjectionStatuses.Warming,
                    0,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null))
            .ToArray();
    }

    private static string ProjectionStatus(
        IReadOnlyList<ResourceTableDatasetInput> inputs)
    {
        if (inputs.Count == 0)
        {
            return ResourceTableProjectionStatuses.Warming;
        }

        if (inputs.All(static input =>
                input.Status == ResourceBreakdownSamplingStatuses.Ready))
        {
            return ResourceTableProjectionStatuses.Ready;
        }

        if (inputs.Any(static input => input.LastSuccessAt is not null))
        {
            return ResourceTableProjectionStatuses.Stale;
        }

        return inputs.Any(static input =>
                input.Status == ResourceBreakdownSamplingStatuses.Failed)
            ? ResourceTableProjectionStatuses.Failed
            : ResourceTableProjectionStatuses.Warming;
    }

    private static IReadOnlyList<ResourceTableColumn> CreateColumns(
        IReadOnlyList<string> requestedColumnIds,
        ResourceBreakdownSnapshot breakdown,
        string viewMode)
    {
        var normalizedViewMode = NormalizeViewMode(viewMode);
        var requested = NormalizeColumnIds(requestedColumnIds, normalizedViewMode);
        var barLabels = breakdown.Bars
            .ToDictionary(static bar => bar.MetricId, static bar => bar.Label, StringComparer.OrdinalIgnoreCase);
        var knownColumns = ResourceTableColumnCatalog.KnownColumns()
            .ToDictionary(static column => column.Id, StringComparer.OrdinalIgnoreCase);
        return requested
            .Select(id => knownColumns.TryGetValue(id, out var column)
                ? column with { Visible = true }
                : ResourceTableColumnCatalog.CreateGpuColumn(id, barLabels.GetValueOrDefault(id)))
            .ToArray();
    }

    public static IReadOnlyList<string> NormalizeColumnIds(IReadOnlyList<string>? requestedColumnIds, string? viewMode = null)
    {
        var normalizedViewMode = NormalizeViewMode(viewMode);
        var requested = requestedColumnIds?
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Where(ResourceTableColumnCatalog.IsKnownColumnId)
            .Where(id => IsColumnAllowedForViewMode(id, normalizedViewMode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        return requested.Length > 0 ? requested : DefaultColumnIds(normalizedViewMode);
    }

    public static IReadOnlyList<string> DefaultColumnIds(string? viewMode = null)
    {
        if (!NormalizeViewMode(viewMode).Equals(ResourceTableViewModes.Process, StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                ResourceTableColumnIds.Name,
                ResourceTableColumnIds.Status,
                ResourceTableColumnIds.Cpu,
                ResourceTableColumnIds.Memory,
                ResourceTableColumnIds.Disk,
                ResourceTableColumnIds.Network
            ];
        }

        return
        [
            ResourceTableColumnIds.Name,
            ResourceTableColumnIds.ProcessId,
            ResourceTableColumnIds.Status,
            ResourceTableColumnIds.User,
            ResourceTableColumnIds.Architecture,
            ResourceTableColumnIds.Cpu,
            ResourceTableColumnIds.Memory,
            ResourceTableColumnIds.Disk,
            ResourceTableColumnIds.Network
        ];
    }

    private static bool IsColumnAllowedForViewMode(string columnId, string viewMode)
    {
        return viewMode.Equals(ResourceTableViewModes.Process, StringComparison.OrdinalIgnoreCase)
            || !DashboardSettingsDefaults.IsProcessDetailColumn(columnId);
    }

    private IReadOnlyList<ResourceTableRow> BuildRows(
        ResourceBreakdownSnapshot breakdown,
        IReadOnlyList<ResourceTableColumn> columns,
        ResourceTableRequest request,
        string projectionStatus)
    {
        var visibleColumnIds = columns.Select(static column => column.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accumulators = new Dictionary<string, RowAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var bar in breakdown.Bars)
        {
            var columnId = ColumnIdForMetric(bar.MetricId);
            if (columnId is null || !visibleColumnIds.Contains(columnId))
            {
                continue;
            }

            foreach (var segment in bar.Software)
            {
                var row = GetSoftwareAccumulator(accumulators, segment);
                row.ApplySoftwareValue(bar, segment);

                foreach (var process in segment.Processes)
                {
                    row.ApplyProcessValue(bar, process);
                }
            }
        }

        var softwareRows = accumulators.Values
            .Select(accumulator => accumulator.ToSoftwareRow(columns, request))
            .ToArray();
        var rowBySoftwareId = accumulators.ToDictionary(
            static item => item.Key,
            static item => item.Value,
            StringComparer.OrdinalIgnoreCase);

        if (NormalizeViewMode(request.ViewMode).Equals(ResourceTableViewModes.Process, StringComparison.OrdinalIgnoreCase))
        {
            var processRows = accumulators.Values
                .SelectMany(accumulator => accumulator.ToFlatProcessRows(columns, request))
                .ToArray();
            return PrependSummaryRow(
                SortRows(processRows, request),
                breakdown,
                columns,
                projectionStatus);
        }

        var sortedSoftwareRows = SortRows(softwareRows, request);
        var rows = new List<ResourceTableRow>();
        foreach (var softwareRow in sortedSoftwareRows)
        {
            rows.Add(softwareRow);
            if (softwareRow.SoftwareId is null
                || (!request.IncludeProcessRows && !request.ExpandedSoftwareIds.Contains(softwareRow.SoftwareId)))
            {
                continue;
            }

            if (!rowBySoftwareId.TryGetValue(softwareRow.SoftwareId, out var accumulator))
            {
                continue;
            }

            rows.AddRange(accumulator.ToProcessRows(columns, softwareRow.Id, request));
        }

        return PrependSummaryRow(rows, breakdown, columns, projectionStatus);
    }

    private static IReadOnlyList<ResourceTableRow> PrependSummaryRow(
        IReadOnlyList<ResourceTableRow> rows,
        ResourceBreakdownSnapshot breakdown,
        IReadOnlyList<ResourceTableColumn> columns,
        string projectionStatus)
    {
        return [CreateSummaryRow(breakdown, columns, projectionStatus), .. rows];
    }

    private static IReadOnlyList<ResourceTableRow> SortRows(
        IReadOnlyList<ResourceTableRow> rows,
        ResourceTableRequest request)
    {
        var columnId = NormalizeSortColumn(request.SortColumnId);
        var direction = NormalizeSortDirection(request.SortDirection);
        if (IsTextSortColumn(columnId))
        {
            return direction.Equals("asc", StringComparison.OrdinalIgnoreCase)
                ? rows.OrderBy(row => TextSortKey(row, columnId), StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
                : rows.OrderByDescending(row => TextSortKey(row, columnId), StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        return direction.Equals("asc", StringComparison.OrdinalIgnoreCase)
            ? rows.OrderBy(row => SortKey(row, columnId)).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
            : rows.OrderByDescending(row => SortKey(row, columnId)).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static double SortKey(ResourceTableRow row, string columnId)
    {
        if (columnId.Equals(ResourceTableSortIds.Impact, StringComparison.OrdinalIgnoreCase))
        {
            return row.ImpactScore;
        }

        return row.SortKeys.GetValueOrDefault(columnId, double.MinValue);
    }

    private static bool IsTextSortColumn(string columnId)
    {
        return columnId.Equals(ResourceTableColumnIds.Name, StringComparison.OrdinalIgnoreCase)
            || columnId.Equals(ResourceTableColumnIds.Status, StringComparison.OrdinalIgnoreCase)
            || columnId.Equals(ResourceTableColumnIds.User, StringComparison.OrdinalIgnoreCase)
            || columnId.Equals(ResourceTableColumnIds.Architecture, StringComparison.OrdinalIgnoreCase);
    }

    private static string TextSortKey(ResourceTableRow row, string columnId)
    {
        if (columnId.Equals(ResourceTableColumnIds.Name, StringComparison.OrdinalIgnoreCase))
        {
            return row.Name;
        }

        if (columnId.Equals(ResourceTableColumnIds.Status, StringComparison.OrdinalIgnoreCase))
        {
            return row.Status;
        }

        return row.Values.TryGetValue(columnId, out var value) ? value.DisplayValue : "";
    }

    private IReadOnlyList<ResourceTableProviderState> ProviderStatesFor(IReadOnlyList<ResourceTableColumn> columns)
    {
        var states = new List<ResourceTableProviderState>();
        foreach (var source in providerStateSources)
        {
            states.AddRange(source.GetStates(columns));
        }

        return states;
    }

    private static string NormalizeSortColumn(string? columnId)
    {
        if (string.IsNullOrWhiteSpace(columnId))
        {
            return ResourceTableSortIds.Impact;
        }

        var normalized = columnId.Trim();
        return normalized.Equals(ResourceTableSortIds.Impact, StringComparison.OrdinalIgnoreCase)
            || ResourceTableColumnCatalog.IsKnownColumnId(normalized)
            ? normalized
            : ResourceTableSortIds.Impact;
    }

    private static string NormalizeSortDirection(string? direction)
    {
        return direction?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true ? "asc" : "desc";
    }

    private static string NormalizeViewMode(string? mode)
    {
        return mode?.Equals(ResourceTableViewModes.Process, StringComparison.OrdinalIgnoreCase) == true
            ? ResourceTableViewModes.Process
            : ResourceTableViewModes.Software;
    }
}
