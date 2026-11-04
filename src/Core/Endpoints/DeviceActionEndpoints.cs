using ResourceManager.App.Infrastructure.DeviceActions;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapDeviceActionEndpoints(this IEndpointRouteBuilder app, StartupCapabilitySet capabilities)
    {
        app.MapGet("/api/device-actions/records", async (long? beforeSequence, int limit, DeviceActionBridge bridge, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await bridge.ReadAsync(beforeSequence, limit, cancellationToken)); }
            catch (ArgumentException) { return Results.BadRequest(new EndpointCodePayload("invalid-device-history-page")); }
        });
        app.MapPost("/api/device-actions/records/history/clear", async (DeviceActionBridge bridge, CancellationToken cancellationToken) =>
            Results.Ok(new ClearReportHistoryResponse(await bridge.ClearHistoryAsync(cancellationToken))));
        if (capabilities.Allows(StartupCapability.MutablePersistence) && capabilities.Allows(StartupCapability.RuntimeEffectOwners))
            app.MapPost("/api/reports/{reportEntryId}/actions/restart-device", async (
                string reportEntryId, DeviceRestartRequest request, DeviceActionBridge bridge, CancellationToken cancellationToken) =>
                Results.Ok(await bridge.ExecuteAsync(reportEntryId, request.Confirmed, cancellationToken)));
        return app;
    }
    public sealed record DeviceRestartRequest(bool Confirmed);
}
