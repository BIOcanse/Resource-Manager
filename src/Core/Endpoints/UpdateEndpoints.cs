using ResourceManager.App.Infrastructure.Updates;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/updates/product/versions", async (
            ProductVersionCatalogService catalog,
            CancellationToken cancellationToken) =>
            Results.Ok(await catalog.ReadAsync(cancellationToken))).AllowAnonymous();
        app.MapGet("/api/updates/product/status", (ProductUpdateCoordinator coordinator) =>
            Results.Ok(coordinator.Status)).AllowAnonymous();
        app.MapPost("/api/updates/product/prepare", (
            ProductUpdateRequest request,
            ProductUpdateCoordinator coordinator) =>
        {
            try
            {
                return Results.Accepted("/api/updates/product/status", coordinator.Submit(request.Choice));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });
        return app;
    }
}

public sealed record ProductUpdateRequest(string Choice);
