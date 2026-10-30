using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapSoftwareProtectionEndpoints(this IEndpointRouteBuilder app)
    {
        // The list: defaults (software_catalog, system_default) and the user's entries in place of the defaults
        // they change (software_detail, report, protection_list).
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

        // Sets one entry's level (0 switches a default off); stored as the user's entry for that target.
        app.MapPut("/api/optimization/protection/entries/{protectionId}/level", async (
            string protectionId,
            OptimizationProtectionLevelRequest request,
            IOptimizationProtectionService protectionService,
            CancellationToken cancellationToken) =>
        {
            if (!OptimizationProtectionLevels.IsValid(request.ProtectionLevel))
            {
                return Results.BadRequest(new EndpointErrorPayload("optimization-protection-level-invalid"));
            }

            var updated = await protectionService.UpdateProtectedTargetLevelAsync(
                protectionId, request.ProtectionLevel, cancellationToken);
            return updated is null
                ? Results.NotFound(new EndpointErrorPayload("optimization-protection-not-found"))
                : Results.Ok(updated);
        });

        // Removes the user's own entry; the default it replaced (if any) is in effect again.
        app.MapDelete("/api/optimization/protection/entries/{protectionId}", async (
            string protectionId,
            IOptimizationProtectionService protectionService,
            CancellationToken cancellationToken) =>
        {
            return await protectionService.RemoveProtectedTargetAsync(protectionId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound(new EndpointErrorPayload("optimization-protection-not-found"));
        });

        return app;
    }
}
