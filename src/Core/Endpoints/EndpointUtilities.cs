using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Application.Migration;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Domain.Migration;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ResourceTable;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    static void DisableResponseCache(HttpResponse response)
    {
        response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Expires"] = "0";
    }

    static IReadOnlyDictionary<string, string> ParseResourceScaleModes(
        HttpRequest request,
        CompiledMonitoringPlan? monitoringPlan = null)
        => ParseResourceScaleModes(request.Query, monitoringPlan);

    static IReadOnlyDictionary<string, string> ParseResourceScaleModes(
        IQueryCollection query,
        CompiledMonitoringPlan? monitoringPlan = null)
    {
        if (!query.TryGetValue("modes", out var modes))
        {
            return monitoringPlan?.ResourceBarScaleModes ?? new Dictionary<string, string>();
        }

        return modes
            .SelectMany(static value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
            .Select(static value =>
            {
                var separator = value.LastIndexOf(':');
                return separator > 0
                    ? new
                    {
                        MetricId = value[..separator],
                        ScaleMode = value[(separator + 1)..]
                    }
                    : null;
            })
            .Where(static item => item is not null
                && DashboardSettingsDefaults.IsResourceBarMetricSupported(item.MetricId))
            .ToDictionary(
                static item => item!.MetricId,
                static item => ResourceBreakdownScaleModes.Normalize(item!.MetricId, item.ScaleMode),
                StringComparer.OrdinalIgnoreCase);
    }

    static IReadOnlyList<string> ParseResourceMetricIds(
        HttpRequest request,
        CompiledMonitoringPlan? monitoringPlan = null)
        => ParseResourceMetricIds(request.Query, monitoringPlan);

    static IReadOnlyList<string> ParseResourceMetricIds(
        IQueryCollection query,
        CompiledMonitoringPlan? monitoringPlan = null)
    {
        return ParseResourceMetricIdsFromQuery(
            query,
            "ids",
            monitoringPlan?.ResourceBarMetricIds);
    }

    static IReadOnlyList<string> ParseResourceSampleMetricIds(HttpRequest request)
        => ParseResourceSampleMetricIds(request.Query);

    static IReadOnlyList<string> ParseResourceSampleMetricIds(IQueryCollection query)
    {
        return ParseResourceMetricIdsFromQuery(
            query,
            "sampleIds",
            fallbackMetricIds: null);
    }

    static IReadOnlySet<string> ParseResourceBreakdownProcessDetailSoftwareIds(HttpRequest request)
        => ParseResourceBreakdownProcessDetailSoftwareIds(request.Query);

    static IReadOnlySet<string> ParseResourceBreakdownProcessDetailSoftwareIds(
        IQueryCollection query)
    {
        return query.TryGetValue("processDetails", out var ids)
            ? ids.SelectMany(static value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    static ProcessSampleDetailLevel ResolveProcessSampleDetailLevel(
        HttpRequest request,
        ResourceTableRequest tableRequest)
        => ResolveProcessSampleDetailLevel(request.Query, tableRequest);

    static ProcessSampleDetailLevel ResolveProcessSampleDetailLevel(
        IQueryCollection query,
        ResourceTableRequest tableRequest)
    {
        if (query.TryGetValue("processDetailLevel", out var explicitValue))
        {
            var parsed = ParseProcessSampleDetailLevel(explicitValue.ToString());
            if (parsed is not null)
            {
                return parsed.Value;
            }
        }

        return tableRequest.ViewMode.Equals(ResourceTableViewModes.Process, StringComparison.OrdinalIgnoreCase)
            || tableRequest.ColumnIds.Contains(ResourceTableColumnIds.User, StringComparer.OrdinalIgnoreCase)
            || tableRequest.ColumnIds.Contains(ResourceTableColumnIds.Architecture, StringComparer.OrdinalIgnoreCase)
            ? ProcessSampleDetailLevel.ResourceTableFull
            : ProcessSampleDetailLevel.ResourceTableBasic;
    }

    static ProcessSampleDetailLevel? ParseProcessSampleDetailLevel(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "lite" or "smart" or "smart-scheduling" => ProcessSampleDetailLevel.SmartSchedulingLite,
            "basic" or "resource-table-basic" => ProcessSampleDetailLevel.ResourceTableBasic,
            "full" or "resource-table-full" => ProcessSampleDetailLevel.ResourceTableFull,
            "diagnostics" or "diagnostics-full" => ProcessSampleDetailLevel.DiagnosticsFull,
            _ => null
        };
    }

    static IReadOnlyList<string> ParseResourceMetricIdsFromQuery(
        IQueryCollection query,
        string queryKey,
        IReadOnlyList<string>? fallbackMetricIds)
    {
        return query.TryGetValue(queryKey, out var ids)
            ? ids.SelectMany(static value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
                .Where(static metricId => DashboardSettingsDefaults.IsResourceBarMetricSupported(metricId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : fallbackMetricIds ?? [];
    }

    static ResourceTableRequest ParseResourceTableRequest(
        HttpRequest request,
        CompiledMonitoringPlan? monitoringPlan = null)
        => ParseResourceTableRequest(request.Query, monitoringPlan);

    static ResourceTableRequest ParseResourceTableRequest(
        IQueryCollection query,
        CompiledMonitoringPlan? monitoringPlan = null)
    {
        var mode = query.TryGetValue("mode", out var modeValue)
            ? ParseResourceTableMode(modeValue.ToString())
            : ResourceTableViewModes.Software;
        var fallbackColumns = monitoringPlan?.ResolveTableColumnIds(mode);
        var columns = query.TryGetValue("columns", out var columnsValue)
            ? columnsValue.SelectMany(static value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : fallbackColumns ?? [];
        var expanded = query.TryGetValue("expanded", out var expandedValue)
            ? expandedValue.SelectMany(static value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includeProcessRows = query.TryGetValue("includeProcesses", out var includeProcesses)
            && includeProcesses.Any(static value => value?.Equals("all", StringComparison.OrdinalIgnoreCase) == true);

        return new ResourceTableRequest(
            ResourceTableProjector.NormalizeColumnIds(columns, mode),
            query.TryGetValue("sort", out var sort) ? ParseSortColumn(sort.ToString()) : ResourceTableSortIds.Impact,
            query.TryGetValue("sort", out var sortDirection) ? ParseSortDirection(sortDirection.ToString()) : "desc",
            mode,
            expanded,
            includeProcessRows);
    }

    static string ParseSortColumn(string value)
    {
        var separator = value.IndexOf(':');
        return separator > 0 ? value[..separator] : value;
    }

    static string ParseSortDirection(string value)
    {
        var separator = value.IndexOf(':');
        return separator > 0 && value[(separator + 1)..].Equals("asc", StringComparison.OrdinalIgnoreCase)
            ? "asc"
            : "desc";
    }

    static string ParseResourceTableMode(string value)
    {
        return value.Equals(ResourceTableViewModes.Process, StringComparison.OrdinalIgnoreCase)
            ? ResourceTableViewModes.Process
            : ResourceTableViewModes.Software;
    }

    static IReadOnlyList<string> AddTableRequiredMetricIds(
        IReadOnlyList<string> metricIds,
        IReadOnlyList<string> columnIds)
    {
        var result = metricIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var metricId in ResourceTableColumnCatalog.RequiredMetricIds(columnIds))
        {
            result.Add(metricId);
        }

        return result.ToArray();
    }
}
