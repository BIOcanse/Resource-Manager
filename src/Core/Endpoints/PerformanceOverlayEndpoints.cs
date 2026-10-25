using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapPerformanceOverlayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/performance-overlay/metrics/catalog", async (
            HardwareMetricCatalogState catalog,
            IComponentManager componentManager,
            CancellationToken cancellationToken) =>
            Results.Ok(MetricCatalog.WithInstalledComponents(
                MetricCatalog.ForPerformanceOverlay(catalog.Current),
                await InstalledComponentIdsAsync(componentManager, cancellationToken))))
            .AllowAnonymous();

        return app;
    }
}
