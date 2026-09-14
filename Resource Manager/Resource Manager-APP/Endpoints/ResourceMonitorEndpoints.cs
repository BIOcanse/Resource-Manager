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
using ResourceManager.App.Application.RuntimeSpecialization;
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
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ResourceTable;
using ResourceManager.App.Endpoints.Transport;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapResourceMonitorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/resource-breakdown/snapshot", async (
            HttpRequest request,
            HttpResponse response,
            IResourceBreakdownSampler sampler,
            IRuntimePlanProvider runtimePlanProvider,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var monitoringPlan = runtimePlanProvider.Current.Monitoring;
            var metricIds = request.Query.TryGetValue("ids", out var ids)
                ? ids.SelectMany(static value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
                    .Where(static metricId => DashboardSettingsDefaults.IsResourceBarMetricSupported(metricId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : monitoringPlan.ResourceBarMetricIds;
            var scaleModes = ParseResourceScaleModes(request, monitoringPlan);
            var processDetailSoftwareIds = ParseResourceBreakdownProcessDetailSoftwareIds(request);
            var snapshot = await sampler.GetSnapshotAsync(metricIds, scaleModes, cancellationToken);
            return Results.Ok(ResourceBreakdownWireSnapshot.Create(
                snapshot,
                metricIds,
                processDetailSoftwareIds));
        }).AllowAnonymous();

        app.MapGet("/api/resource-monitor/snapshot", async (
            HttpRequest request,
            HttpResponse response,
            IResourceBreakdownSampler breakdownSampler,
            IResourceTableProjector tableProjector,
            IRuntimePlanProvider runtimePlanProvider,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var monitoringPlan = runtimePlanProvider.Current.Monitoring;
            var scope = ParseResourceMonitorScope(request);
            var metricIds = scope == ResourceMonitorScope.Table
                ? []
                : ParseResourceMetricIds(request, monitoringPlan);
            var requestedSampleMetricIds = ParseResourceSampleMetricIds(request);
            var scaleModes = ParseResourceScaleModes(request, monitoringPlan);
            var tableRequest = ParseResourceTableRequest(request, monitoringPlan);
            var processDetailSoftwareIds = ParseResourceBreakdownProcessDetailSoftwareIds(request);
            var sampleMetricIds = AddTableRequiredMetricIds(
                requestedSampleMetricIds.Count > 0 ? requestedSampleMetricIds : metricIds,
                scope == ResourceMonitorScope.Bars ? [] : tableRequest.ColumnIds);
            var sampledBreakdown = await breakdownSampler.GetSnapshotAsync(
                new ResourceBreakdownSampleRequest(
                    sampleMetricIds,
                    scaleModes,
                    ResolveProcessSampleDetailLevel(request, tableRequest)),
                cancellationToken);
            var table = tableProjector.Project(sampledBreakdown, tableRequest);
            var breakdownWire = ResourceBreakdownWireSnapshot.Create(
                sampledBreakdown,
                metricIds,
                processDetailSoftwareIds);
            return Results.Ok(new ResourceMonitorWireSnapshot(
                ResourceMonitorWireSnapshot.CurrentVersion,
                MaxTimestamp(
                    breakdownWire.CapturedAt,
                    table.CapturedAt),
                breakdownWire,
                ResourceTableWireSnapshot.Create(table)));
        }).AllowAnonymous();

        app.MapGet("/api/resource-table/snapshot", async (
            HttpRequest request,
            HttpResponse response,
            IResourceBreakdownSampler breakdownSampler,
            IResourceTableProjector tableProjector,
            IRuntimePlanProvider runtimePlanProvider,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var monitoringPlan = runtimePlanProvider.Current.Monitoring;
            var tableRequest = ParseResourceTableRequest(request, monitoringPlan);
            var metricIds = AddTableRequiredMetricIds(ParseResourceMetricIds(request, monitoringPlan), tableRequest.ColumnIds);
            var scaleModes = ParseResourceScaleModes(request, monitoringPlan);
            var breakdown = await breakdownSampler.GetSnapshotAsync(
                new ResourceBreakdownSampleRequest(
                    metricIds,
                    scaleModes,
                    ResolveProcessSampleDetailLevel(request, tableRequest)),
                cancellationToken);
            return Results.Ok(ResourceTableWireSnapshot.Create(
                tableProjector.Project(breakdown, tableRequest)));
        }).AllowAnonymous();

        return app;
    }

    private static DateTimeOffset? MaxTimestamp(
        DateTimeOffset? left,
        DateTimeOffset? right)
        => left is null
            ? right
            : right is null || left >= right
                ? left
                : right;

    private static ResourceMonitorScope ParseResourceMonitorScope(HttpRequest request)
        => ParseResourceMonitorScope(request.Query);

    private static ResourceMonitorScope ParseResourceMonitorScope(
        IQueryCollection query)
    {
        if (!query.TryGetValue("scope", out var value))
        {
            return ResourceMonitorScope.Combined;
        }

        return value.ToString().Trim().ToLowerInvariant() switch
        {
            "bars" => ResourceMonitorScope.Bars,
            "table" => ResourceMonitorScope.Table,
            _ => ResourceMonitorScope.Combined
        };
    }

    private enum ResourceMonitorScope : byte
    {
        Combined = 0,
        Bars = 1,
        Table = 2
    }
}
