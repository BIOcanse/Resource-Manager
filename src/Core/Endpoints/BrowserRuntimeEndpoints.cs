using ResourceManager.App.Application.BrowserRuntimes;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapBrowserRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/browser-runtimes", async (
            bool? refresh,
            IBrowserRuntimeCatalog catalog,
            CancellationToken cancellationToken) =>
            Results.Ok(await catalog.GetSnapshotAsync(refresh == true, cancellationToken))).AllowAnonymous();

        app.MapGet("/api/public/v1/browser-runtimes", async (
            IBrowserRuntimeCatalog catalog,
            CancellationToken cancellationToken) =>
            Results.Ok(await catalog.GetSnapshotAsync(forceRefresh: false, cancellationToken))).AllowAnonymous();

        return app;
    }
}
