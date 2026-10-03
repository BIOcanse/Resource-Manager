using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.Overlay;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapPerformanceOverlayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/performance-overlay/default", (string softwareId) =>
        {
            try { return Results.Ok(PerformanceOverlaySettings.Normalize(null, softwareId)); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).AllowAnonymous();

        app.MapGet("/api/performance-overlay/metrics/catalog", (
            DashboardMonitoringCatalogState catalog) =>
            Results.Ok(MetricCatalog.ForPerformanceOverlay(catalog.Current)))
            .AllowAnonymous();

        app.MapGet("/api/performance-overlay/software/{softwareId}", async (
            string softwareId,
            IPerformanceOverlaySettingsStore store,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await store.GetSoftwareAsync(softwareId, cancellationToken)); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).AllowAnonymous();

        app.MapPut("/api/performance-overlay/software/{softwareId}", async (
            string softwareId,
            PerformanceOverlaySettings settings,
            IPerformanceOverlaySettingsStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await store.SaveSoftwareAsync(
                    settings with { SoftwareId = softwareId }, cancellationToken));
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        return app;
    }
}
