using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapOptimizationProtectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/optimization/trust", async (
            IHostManagerReportService reportService,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await reportService.GetTrustedTargetsAsync(cancellationToken));
        });

        app.MapGet("/api/optimization/protection", async (
            IOptimizationProtectionService protectionService,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await protectionService.GetProtectedTargetsAsync(cancellationToken));
        });

        app.MapPost("/api/optimization/trust/refresh", async (
            IHostManagerReportService reportService,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await reportService.RefreshTrustedTargetsAsync(cancellationToken));
        });

        app.MapPost("/api/optimization/protection/refresh", async (
            IOptimizationProtectionService protectionService,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(
                await protectionService.RefreshProtectedTargetsAsync(
                    cancellationToken));
        });

        app.MapPost("/api/optimization/protection/{id}/level", async (
            string id,
            OptimizationProtectionLevelRequest request,
            IOptimizationProtectionService protectionService,
            CancellationToken cancellationToken) =>
        {
            var protectedTarget =
                await protectionService.UpdateProtectedTargetLevelAsync(
                id,
                request.ProtectionLevel,
                cancellationToken);
            return protectedTarget is null
                ? Results.NotFound(new { error = "保护项不存在。" })
                : Results.Ok(protectedTarget);
        });

        app.MapDelete("/api/optimization/trust/{id}", async (
            string id,
            IHostManagerReportService reportService,
            CancellationToken cancellationToken) =>
        {
            return await reportService.RemoveTrustedTargetAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound(new { error = "信任项不存在。" });
        });

        app.MapDelete("/api/optimization/protection/{id}", async (
            string id,
            IOptimizationProtectionService protectionService,
            CancellationToken cancellationToken) =>
        {
            return await protectionService.RemoveProtectedTargetAsync(
                id,
                cancellationToken)
                ? Results.NoContent()
                : Results.NotFound(new { error = "保护项不存在。" });
        });

        app.MapPost("/api/optimization/protection/placement/preview", async (
            OptimizationProtectionPlacementRequest request,
            IOptimizationProtectionPlacementService placementService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await placementService.PreviewAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/optimization/protection/placement/apply", async (
            OptimizationProtectionPlacementRequest request,
            IOptimizationProtectionPlacementService placementService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await placementService.ApplyAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/optimization/protection/placement/records", async (
            IOptimizationProtectionPlacementService placementService,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await placementService.GetRecordsAsync(cancellationToken));
        });

        app.MapPost("/api/optimization/protection/placement/restore", async (
            OptimizationProtectionPlacementRestoreRequest request,
            IOptimizationProtectionPlacementService placementService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await placementService.RestoreAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }
}
