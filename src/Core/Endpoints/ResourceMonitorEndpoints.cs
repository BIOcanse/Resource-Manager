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
using ResourceManager.App.Application.RuntimeSpecialization;
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
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints.Transport;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapResourceMonitorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/resource-breakdown/fields/catalog", () => Results.Ok(ProcessMetricFieldRegistry.Definitions))
            .AllowAnonymous();
        app.MapGet("/api/resource-breakdown/snapshot", async (
            HttpRequest request,
            HttpResponse response,
            IResourceBreakdownSampler sampler,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var metricIds = request.Query.TryGetValue("ids", out var ids)
                ? ids.SelectMany(static value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
                    .Where(IsResourceSampleMetricSupported)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
            var scaleModes = ParseResourceScaleModes(request);
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
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var scope = ParseResourceMonitorScope(request);
            var metricIds = ParseResourceMetricIds(request);
            var scaleModes = ParseResourceScaleModes(request);
            var processDetailSoftwareIds = ParseResourceBreakdownProcessDetailSoftwareIds(request);
            if (scope == ResourceMonitorScope.Details)
            {
                var details = await breakdownSampler.GetSnapshotAsync(
                    new ResourceBreakdownSampleRequest(
                        metricIds,
                        scaleModes,
                        ProcessSampleDetailLevel.ResourceTableBasic),
                    cancellationToken);
                return Results.Ok(ResourceProcessDetailsWireSnapshot.Create(
                    details,
                    metricIds,
                    processDetailSoftwareIds));
            }

            var sampledBreakdown = await breakdownSampler.GetSnapshotAsync(
                new ResourceBreakdownSampleRequest(
                    metricIds,
                    scaleModes,
                    ProcessSampleDetailLevel.ResourceTableBasic),
                cancellationToken);
            var breakdownWire = ResourceBreakdownWireSnapshot.Create(
                sampledBreakdown,
                metricIds,
                processDetailSoftwareIds);
            return Results.Ok(new ResourceMonitorWireSnapshot(
                ResourceMonitorWireSnapshot.CurrentVersion,
                breakdownWire.CapturedAt,
                breakdownWire));
        }).AllowAnonymous();

        app.MapGet("/api/resource-facts/snapshot", async (
            HttpRequest request,
            HttpResponse response,
            IResourceBreakdownSampler breakdownSampler,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            ResourceBreakdownSampleRequest sampleRequest;
            try
            {
                sampleRequest = ParseResourceFactsRequest(request.Query);
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new EndpointErrorPayload(error.Message));
            }
            if (sampleRequest.MetricIds.Count == 0 && sampleRequest.PublicationDatasetIds.Count == 0)
            {
                return Results.Ok(ResourceFactsSnapshot.Empty);
            }
            var breakdown = await breakdownSampler.GetSnapshotAsync(
                sampleRequest,
                cancellationToken);
            return Results.Ok(ResourceFactsSnapshot.Create(breakdown, sampleRequest.MetricIds,
                ReadResourceFoundations(sampleRequest, request.HttpContext.RequestServices)));
        }).AllowAnonymous();

        return app;
    }

    private static ResourceBreakdownSampleRequest ParseResourceFactsRequest(IQueryCollection query)
    {
        var items = query.TryGetValue("items", out var values)
            ? values.SelectMany(static value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        var foundations = items.Where(static item => item is Domain.Monitoring.SamplingDatasetIds.ProcessInventory
            or Domain.Monitoring.SamplingDatasetIds.ProcessAttribution).ToArray();
        var metrics = items.Except(foundations).ToArray();
        foreach (var item in metrics)
        {
            if (!IsResourceFactsItemSupported(item))
            {
                throw new ArgumentException($"Resource facts item '{item}' is unsupported.");
            }
        }
        return new(metrics,
            new Dictionary<string, string>(),
            query.TryGetValue("processDetailLevel", out var detail)
                ? ParseProcessSampleDetailLevel(detail.ToString()) ?? ProcessSampleDetailLevel.ResourceTableBasic
                : ProcessSampleDetailLevel.ResourceTableBasic) { PublicationDatasetIds = foundations };
    }

    private static SchedulingProcessFactSnapshot? ReadResourceFoundations(ResourceBreakdownSampleRequest request, IServiceProvider services)
        => request.PublicationDatasetIds.Count == 0 ? null
            : services.GetRequiredService<ISchedulingProcessFactObservationSource>().ReadLatest(new(
                SchedulingProcessMetricMask.None, new SchedulingGpuInventorySnapshot(
                    SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, [])));

    private static bool IsResourceFactsItemSupported(string item)
        => ResourceBreakdownScaleModes.SupportsCapacity(item)
            || item.ToLowerInvariant() is ResourceBreakdownMetricIds.DiskIo
                or ResourceBreakdownMetricIds.DiskRead or ResourceBreakdownMetricIds.DiskWrite
                or ResourceBreakdownMetricIds.NetworkTraffic or ResourceBreakdownMetricIds.NetworkReceive
                or ResourceBreakdownMetricIds.NetworkSend or ResourceBreakdownMetricIds.NetworkRawTraffic
                or ResourceBreakdownMetricIds.NetworkRawReceive or ResourceBreakdownMetricIds.NetworkRawSend;

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
            "details" => ResourceMonitorScope.Details,
            _ => ResourceMonitorScope.Combined
        };
    }

    private enum ResourceMonitorScope : byte
    {
        Combined = 0,
        Bars = 1,
        Details = 3
    }
}
