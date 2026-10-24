using ResourceManager.App.Infrastructure.Updates;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/updates/product/versions", async (
            ProductUpdateCoordinator coordinator,
            CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.CheckCatalogAsync(cancellationToken))).AllowAnonymous();
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
            catch (ProductUpdateFailure exception)
            {
                return Results.Json(new { errorCode = exception.Code }, statusCode: exception.Code == "update.busy" ? 409 : 400);
            }
        });
        app.MapPost("/api/updates/product/apply", async (ProductUpdateCoordinator coordinator, CancellationToken token) =>
        {
            try { return Results.Accepted("/api/updates/product/status", await coordinator.ApplyAsync(token)); }
            catch (ProductUpdateFailure exception)
            {
                return Results.Json(new { errorCode = exception.Code }, statusCode: exception.Code == "update.busy" ? 409 : 400);
            }
        });
        return app;
    }
}

public sealed record ProductUpdateRequest(string Choice);
