using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.MemoryProtection;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static void MapHostMemoryProtectionEndpoints(this IEndpointRouteBuilder app, StartupCapabilitySet capabilities)
    {
        app.MapGet("/api/memory-protection/normal-running-point", async (HostMemoryProtectionService protection, HttpResponse response, CancellationToken token)
            => await response.WriteAsJsonAsync(protection.ReadPoint(), token));
        // Points are saved user settings, so creating, selecting and deleting them needs a host that may persist.
        if (capabilities.Allows(StartupCapability.MutablePersistence))
        app.MapPost("/api/memory-protection/normal-running-point", async (HostMemoryProtectionService protection, CancellationToken token) =>
        {
            try { return Results.Ok(await protection.CreatePointAsync(token)); }
            catch (NormalRunningPointException error) { return Results.Json(new EndpointErrorPayload(error.Reason), statusCode: 409); }
        });
        app.MapGet("/api/memory-protection/normal-running-points", (HostMemoryProtectionService protection) => Results.Ok(protection.ListPoints()));
        if (capabilities.Allows(StartupCapability.MutablePersistence))
        app.MapPut("/api/memory-protection/normal-running-points/active", async (NormalRunningPointSelection request, HostMemoryProtectionService protection)
            => await protection.SelectPointAsync(request.Id) is { } selected ? Results.Ok(selected)
                : Results.NotFound(new EndpointErrorPayload("normal-running-point-not-found")));
        if (capabilities.Allows(StartupCapability.MutablePersistence))
        app.MapDelete("/api/memory-protection/normal-running-points/{id}", async (string id, HostMemoryProtectionService protection)
            => await protection.DeletePointAsync(id) ? Results.NoContent()
                : Results.NotFound(new EndpointErrorPayload("normal-running-point-not-found")));
        app.MapGet("/api/memory-protection", async (HostMemoryProtectionService protection, CancellationToken token)
            => Results.Ok(await protection.ReadStatusAsync(token)));
        // Every action record is history. Clearing removes the records only; slice files stay for the user.
        app.MapPost("/api/memory-protection/records/history/clear", (HostMemoryProtectionService protection)
            => Results.Ok(new ClearReportHistoryResponse(protection.ClearHistory())));
        if (capabilities.Allows(StartupCapability.MutablePersistence))
        app.MapPost("/api/memory-protection/reserve/rebuild", async (HostMemoryProtectionService protection, CancellationToken token) =>
        {
            var status = await protection.RebuildReserveAsync(token);
            return Results.Json(status, statusCode: status.Reserve.Built ? 200 : 409);
        });
        // The existing revision-checked patch protocol prevents overwriting another settings consumer.
        if (capabilities.Allows(StartupCapability.MutablePersistence))
        app.MapPatch("/api/memory-protection", async (AppSettingsPatchRequest request, IRuntimeSpecializationCoordinator runtime, CancellationToken token)
            =>
            {
                if (request.Changes.ValueKind != System.Text.Json.JsonValueKind.Object
                    || request.Changes.EnumerateObject().Count() != 1 || !request.Changes.TryGetProperty("memoryProtection", out _))
                    return Results.BadRequest(new EndpointErrorPayload("expected-memory-protection-settings-patch"));
                try
                {
                    var result = await runtime.ApplyAppSettingsPatchAsync(request, "memory-protection-settings-patched", token);
                    return result.RuntimeApplicationDisposition switch
                    {
                        "committedAndApplied" or "committedWithCapabilityConstraints" => Results.Ok(result),
                        "committedWithDeliveryFailures" or "savedNotApplied" => Results.Json(result, statusCode: 202),
                        _ => Results.Json(result, statusCode: 409)
                    };
                }
                catch (AppSettingsPatchException error)
                { return Results.BadRequest(new EndpointErrorPayload(error.Message)); }
            });
    }
}
