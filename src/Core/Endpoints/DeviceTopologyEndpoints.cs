using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapDeviceTopologyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/device-topology/state", async (
            HttpResponse response,
            IDeviceTopologySnapshotProvider provider, CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await provider.FetchOnceAsync("device-topology:state", cancellationToken));
        }).AllowAnonymous();

        app.MapGet("/api/device-topology/fields", async (
            HttpResponse response,
            IDeviceTopologySnapshotProvider provider, CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var state = await provider.FetchOnceAsync("device-topology:fields", cancellationToken);
            return Results.Ok(state?.InformationFields ?? DeviceInformationSnapshot.Empty);
        }).AllowAnonymous();

        app.MapGet("/api/device-topology/acpi/fields", async (
            HttpResponse response,
            IDeviceTopologySnapshotProvider provider, CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await provider.FetchAcpiOnceAsync("device-topology:acpi-fields", cancellationToken)
                ?? DeviceInformationSnapshot.Empty);
        }).AllowAnonymous();

        app.MapNvidiaDrsInformationEndpoints();
        return app;
    }
}
