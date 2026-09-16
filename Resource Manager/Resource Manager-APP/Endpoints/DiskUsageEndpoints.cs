using ResourceManager.App.Application.DiskUsage;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapDiskUsageEndpoints(this IEndpointRouteBuilder app)
    {
        // 卷会插拔，所以每次都现读，不缓存也不缓存响应。
        app.MapGet("/api/disk-usage/volumes", (
            HttpResponse response,
            IDiskUsageVolumeCatalog volumes) =>
        {
            DisableResponseCache(response);
            return Results.Ok(volumes.ReadVolumes());
        }).AllowAnonymous();

        return app;
    }
}
