using ResourceManager.App.Application.DeviceTopology;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapDeviceTopologyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/device-topology/state", (
            HttpResponse response,
            IDeviceTopologySnapshotProvider provider) =>
        {
            DisableResponseCache(response);
            return Results.Ok(provider.ReadState());
        }).AllowAnonymous();

        return app;
    }
}
