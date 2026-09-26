using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapOptimizationGradeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/optimization/a1/preview", async (
            OptimizationA1Request request,
            IOptimizationA1Service a1Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await a1Service.PreviewAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/optimization/a1/apply", async (
            OptimizationA1Request request,
            IOptimizationA1Service a1Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await a1Service.ApplyAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/optimization/a1/records", async (
            IOptimizationA1Service a1Service,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await a1Service.GetRecordsAsync(cancellationToken));
        });

        app.MapPost("/api/optimization/a1/restore", async (
            OptimizationA1RestoreRequest request,
            IOptimizationA1Service a1Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await a1Service.RestoreAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/optimization/a2/preview", async (
            OptimizationA2Request request,
            IOptimizationA2Service a2Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await a2Service.PreviewAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/optimization/a2/apply", async (
            OptimizationA2Request request,
            IOptimizationA2Service a2Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await a2Service.ApplyAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/optimization/a2/records", async (
            IOptimizationA2Service a2Service,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await a2Service.GetRecordsAsync(cancellationToken));
        });

        app.MapPost("/api/optimization/a2/restore", async (
            OptimizationA2RestoreRequest request,
            IOptimizationA2Service a2Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await a2Service.RestoreAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/optimization/level1/preview", async (
            OptimizationLevel1Request request,
            IOptimizationLevel1Service level1Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await level1Service.PreviewAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/optimization/level1/apply", async (
            OptimizationLevel1Request request,
            IOptimizationLevel1Service level1Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await level1Service.ApplyAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/optimization/level1/records", async (
            IOptimizationLevel1Service level1Service,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await level1Service.GetRecordsAsync(cancellationToken));
        });

        app.MapPost("/api/optimization/level1/restore", async (
            OptimizationLevel1RestoreRequest request,
            IOptimizationLevel1Service level1Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await level1Service.RestoreAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/optimization/level2/preview", async (
            OptimizationLevel2Request request,
            IOptimizationLevel2Service level2Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await level2Service.PreviewAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/optimization/level2/apply", async (
            OptimizationLevel2Request request,
            IOptimizationLevel2Service level2Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await level2Service.ApplyAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/optimization/level2/records", async (
            IOptimizationLevel2Service level2Service,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await level2Service.GetRecordsAsync(cancellationToken));
        });

        app.MapPost("/api/optimization/level2/restore", async (
            OptimizationLevel2RestoreRequest request,
            IOptimizationLevel2Service level2Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await level2Service.RestoreAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/optimization/level3/preview", async (
            OptimizationLevel3Request request,
            IOptimizationLevel3Service level3Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await level3Service.PreviewAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/optimization/level3/apply", async (
            OptimizationLevel3Request request,
            IOptimizationLevel3Service level3Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await level3Service.ApplyAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/optimization/level3/records", async (
            IOptimizationLevel3Service level3Service,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await level3Service.GetRecordsAsync(cancellationToken));
        });

        app.MapPost("/api/optimization/level3/restore", async (
            OptimizationLevel3RestoreRequest request,
            IOptimizationLevel3Service level3Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await level3Service.RestoreAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/optimization/level4/preview", async (
            OptimizationLevel4Request request,
            IOptimizationLevel4Service level4Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await level4Service.PreviewAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/optimization/level4/apply", async (
            OptimizationLevel4Request request,
            IOptimizationLevel4Service level4Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await level4Service.ApplyAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/optimization/level4/records", async (
            IOptimizationLevel4Service level4Service,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await level4Service.GetRecordsAsync(cancellationToken));
        });

        app.MapPost("/api/optimization/level4/restore", async (
            OptimizationLevel4RestoreRequest request,
            IOptimizationLevel4Service level4Service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await level4Service.RestoreAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }
}
