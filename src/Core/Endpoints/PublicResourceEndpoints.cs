using ResourceManager.App.Application.PublicResources;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapPublicResourceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/public/v1/capability", (
            IHostPublicResourceCapability capability) =>
            Results.Ok(capability.GetCapability())).AllowAnonymous();

        app.MapGet("/api/public/v1/resources", (
            IPublicResourceDirectoryQueries queries,
            IHostPublicResourceCapability capability) =>
        {
            var state = capability.GetCapability();
            return state.Available
                ? Results.Ok(queries.GetCatalog())
                : Results.Json(state, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        app.MapGet("/api/public/v1/resources/transport", (
            IPublicResourceDirectoryQueries queries,
            IHostPublicResourceCapability capability) =>
        {
            var state = capability.GetCapability();
            return state.Available
                ? Results.Ok(queries.GetTransportSummary())
                : Results.Json(state, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        app.MapGet("/api/public/v1/resources/{publicResourceId}", (
            ulong publicResourceId,
            IPublicResourceDirectoryQueries queries,
            IHostPublicResourceCapability capability) =>
        {
            var state = capability.GetCapability();
            if (!state.Available)
            {
                return Results.Json(state, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var resource = queries.Find(publicResourceId);
            return resource is null ? Results.NotFound() : Results.Ok(resource);
        }).AllowAnonymous();

        return app;
    }
}
