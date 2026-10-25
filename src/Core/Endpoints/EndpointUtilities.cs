using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.LocalSystem;
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
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;

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
        HttpRequest request)
        => ParseResourceScaleModes(request.Query);

    static IReadOnlyDictionary<string, string> ParseResourceScaleModes(
        IQueryCollection query)
    {
        if (!query.TryGetValue("modes", out var modes))
        {
            return new Dictionary<string, string>();
        }

        return modes
            .SelectMany(static value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
            .Select(static value =>
            {
                var separator = value.LastIndexOf(':');
                return separator > 0
                    ? new MetricScalePayload(value[..separator], value[(separator + 1)..])
                    : null;
            })
            .Where(static item => item is not null
                && (ResourceBreakdownMetricIds.IsSupported(item.MetricId)
                    || ResourceBreakdownScaleModes.SupportsCapacity(item.MetricId)
                    || ProcessMemoryFactFields.TotalMetricId(item.MetricId) is not null))
            .ToDictionary(
                static item => item!.MetricId,
                static item => ResourceBreakdownScaleModes.Normalize(
                    ProcessMemoryFactFields.TotalMetricId(item!.MetricId) ?? item.MetricId, item.ScaleMode),
                StringComparer.OrdinalIgnoreCase);
    }

    static IReadOnlyList<string> ParseResourceMetricIds(
        HttpRequest request)
        => ParseResourceMetricIds(request.Query);

    static IReadOnlyList<string> ParseResourceMetricIds(
        IQueryCollection query)
    {
        return ParseResourceMetricIdsFromQuery(
            query,
            "ids",
            null);
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
                .Where(IsResourceSampleMetricSupported)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : fallbackMetricIds ?? [];
    }

    private static bool IsResourceSampleMetricSupported(string metricId)
        => ResourceBreakdownMetricIds.IsSupported(metricId)
            || ProcessMetricFieldRegistry.Find(metricId) is not null;

}
