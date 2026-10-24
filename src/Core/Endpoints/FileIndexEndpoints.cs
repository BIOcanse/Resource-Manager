using ResourceManager.App.Application.Indexing;
using ResourceManager.App.Infrastructure.Indexing;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapFileIndexEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/index/files/search", async (
            string query,
            int? limit,
            HttpContext context,
            ISoftwareFileIndex fileIndex,
            CancellationToken cancellationToken) =>
        {
            if (!WindowsFileIndexCallerAccess.TryOpenCallerToken(context, out var token)) return Results.StatusCode(403);
            using var callerToken = token;
            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.BadRequest(new { error = "搜索内容不能为空。" });
            }

            return Results.Ok(await fileIndex.SearchAsync(query, Math.Clamp(limit ?? 50, 1, 200), cancellationToken,
                WindowsFileIndexCallerAccess.CreatePathFilter(callerToken)));
        });

        app.MapGet("/api/index/software/{softwareId}", async (
            string softwareId,
            HttpContext context,
            ISoftwareFileIndex fileIndex,
            CancellationToken cancellationToken) =>
        {
            if (!WindowsFileIndexCallerAccess.TryOpenCallerToken(context, out var token)) return Results.StatusCode(403);
            using var callerToken = token;
            var snapshot = await fileIndex.GetSnapshotAsync(softwareId, cancellationToken,
                WindowsFileIndexCallerAccess.CreatePathFilter(callerToken));
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        });

        return app;
    }
}
