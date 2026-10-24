using ResourceManager.App.Application.Indexing;
using ResourceManager.App.Application.PublicServices;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapPublicServiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/public/v1", async (
            ILocalServiceCatalogQueries catalog,
            CancellationToken cancellationToken) =>
            Results.Ok(await catalog.GetCatalogAsync(cancellationToken))).AllowAnonymous();

        app.MapGet("/api/public/v1/capabilities", async (
            ILocalServiceCatalogQueries catalog,
            CancellationToken cancellationToken) =>
            Results.Ok(await catalog.ListCapabilitiesAsync(cancellationToken))).AllowAnonymous();

        app.MapGet("/api/public/v1/index/status", async (
            ISoftwareFileIndex fileIndex,
            CancellationToken cancellationToken) =>
            Results.Ok(await fileIndex.GetStatisticsAsync(cancellationToken))).AllowAnonymous();

        app.MapGet("/api/public/v1/index/files/search", async (
            string query,
            int? limit,
            HttpContext context,
            ISoftwareFileIndex fileIndex,
            CancellationToken cancellationToken) =>
        {
            if (!ResourceManager.App.Infrastructure.Indexing.WindowsFileIndexCallerAccess.TryOpenCallerToken(context, out var token)) return Results.StatusCode(403);
            using var callerToken = token;
            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.BadRequest(new { error = "query-required" });
            }

            return Results.Ok(await fileIndex.SearchAsync(
                query,
                Math.Clamp(limit ?? 50, 1, 200),
                cancellationToken,
                ResourceManager.App.Infrastructure.Indexing.WindowsFileIndexCallerAccess.CreatePathFilter(callerToken)));
        });

        app.MapGet("/api/public/v1/index/software/{softwareId}", async (
            string softwareId,
            HttpContext context,
            ISoftwareFileIndex fileIndex,
            CancellationToken cancellationToken) =>
        {
            if (!ResourceManager.App.Infrastructure.Indexing.WindowsFileIndexCallerAccess.TryOpenCallerToken(context, out var token)) return Results.StatusCode(403);
            using var callerToken = token;
            var snapshot = await fileIndex.GetSnapshotAsync(softwareId, cancellationToken,
                ResourceManager.App.Infrastructure.Indexing.WindowsFileIndexCallerAccess.CreatePathFilter(callerToken));
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        });

        return app;
    }
}
