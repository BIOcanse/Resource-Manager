using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
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
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.ResourceTable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMetricFieldEndpoints();
        app.MapGet("/api/metrics/catalog", (
            DashboardMonitoringCatalogState catalogState) =>
        {
            // Keep unavailable definitions so consumers can display the missing capability.
            return Results.Ok(catalogState.Current is not { } snapshot
                ? Array.Empty<MetricDefinition>()
                : MetricCatalog.FromSnapshot(snapshot));
        }).AllowAnonymous();

        app.MapGet("/api/metrics/snapshot", async (
            HttpRequest request,
            HttpResponse response,
            IMetricSampler sampler,
            IRuntimePlanProvider runtimePlanProvider,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var snapshotRequest = request.Query.TryGetValue("ids", out var ids)
                ? MetricSampleRequest.ForIds(ids)
                : runtimePlanProvider.Current.Monitoring.DashboardMetricRequest;
            var snapshot = await sampler.GetSnapshotAsync(snapshotRequest, cancellationToken);
            var presentation = MetricCatalog.ApplyPresentation(snapshot);
            return Results.Ok(MetricSnapshotWireSnapshot.From(presentation, snapshotRequest));
        }).AllowAnonymous();

        app.MapGet("/api/metrics/gpu-specialized", (
            HttpResponse response,
            IGpuTelemetryWorkerClient gpuTelemetry,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            cancellationToken.ThrowIfCancellationRequested();
            return Results.Ok(GpuTelemetryWireSnapshot.From(gpuTelemetry.GetLatestSnapshot()));
        }).AllowAnonymous();
        return app;
    }

    private static IEndpointRouteBuilder MapMetricFieldEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/metrics/fields/capabilities", ([Microsoft.AspNetCore.Mvc.FromServices] ResourceManager.ReadWrite.ReadWriteCore core)
            => Results.Ok(PublicCapabilityFields(core.Catalog.Fields))).AllowAnonymous();

        app.MapGet("/api/metrics/fields/catalog", (DashboardMonitoringCatalogState catalogState, HttpContext context)
            => catalogState.Current is { } snapshot
                ? Results.Ok(ReadMetricFieldCatalog(snapshot, context.RequestServices))
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();

        app.MapGet("/api/metrics/fields/snapshot", async (
            HttpRequest request, HttpResponse response, DashboardMonitoringCatalogState catalogState,
            IMetricSampler sampler, CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            string? device;
            try { device = ParseMetricFieldDevice(request.Query); }
            catch (ArgumentException) { return Results.BadRequest(); }
            var fields = ParseMetricFieldNames(request.Query);
            if (catalogState.Current is not { } catalog) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            MetricSampleRequest selection;
            try { selection = MetricDeviceFields.CreateRequest(catalog, device, fields); }
            catch (ArgumentException) { return Results.BadRequest(); }
            var snapshot = await sampler.GetSnapshotAsync(selection, cancellationToken);
            return Results.Ok(ReadMetricFields(snapshot, device, fields, request.HttpContext.RequestServices));
        }).AllowAnonymous();

        return app;
    }

    internal static IReadOnlyList<FieldCapability> PublicCapabilityFields(
        IEnumerable<FieldCapability> fields)
        => fields.Where(static field => !string.Equals(
            field.ReadSource,
            DeviceInformationFieldRegistry.MftSourceId,
            StringComparison.Ordinal)).ToArray();

    private static TimeSpan ParseSamplingSubscriptionInterval(
        IQueryCollection query)
    {
        const int defaultIntervalMilliseconds = 1_000;
        if (!query.TryGetValue("intervalMs", out var raw)
            || !int.TryParse(raw.FirstOrDefault(), out var milliseconds))
        {
            milliseconds = defaultIntervalMilliseconds;
        }
        return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 100, 60_000));
    }

    private static string? ParseMetricFieldDevice(IQueryCollection query)
    {
        if (!query.TryGetValue("device", out var values)) return null;
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
            throw new ArgumentException("metric-field-device-invalid", nameof(query));
        return values[0];
    }

    private static IReadOnlySet<string> ParseMetricFieldNames(IQueryCollection query)
        => query["fields"].SelectMany(static value => (value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);
}
