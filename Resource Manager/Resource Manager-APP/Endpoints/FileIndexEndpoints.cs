using ResourceManager.App.Application.Indexing;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapFileIndexEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/index/files/search", async (
            string query,
            int? limit,
            ISoftwareFileIndex fileIndex,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.BadRequest(new { error = "搜索内容不能为空。" });
            }

            return Results.Ok(await fileIndex.SearchAsync(query, limit ?? 50, cancellationToken));
        }).AllowAnonymous();

        app.MapGet("/api/index/software/{softwareId}", async (
            string softwareId,
            ISoftwareFileIndex fileIndex,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await fileIndex.GetSnapshotAsync(softwareId, cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).AllowAnonymous();

        return app;
    }
}
