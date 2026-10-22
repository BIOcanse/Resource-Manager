using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapSoftwareProtectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/optimization/protection", async (
            HttpResponse response,
            IOptimizationProtectionService protectionService,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await protectionService.GetProtectedTargetsAsync(cancellationToken));
        });

        app.MapPut("/api/optimization/protection/software/{softwareId}", async (
            string softwareId,
            SoftwareProtectionRequest request,
            IOptimizationProtectionService protectionService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await protectionService.SetSoftwareProtectionAsync(
                    softwareId, request.Enabled, cancellationToken));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        return app;
    }
}
