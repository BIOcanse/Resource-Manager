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
        app.MapPost("/api/memory-protection/normal-running-point", async (HostMemoryProtectionService protection, CancellationToken token)
            => Results.Ok(await protection.CreatePointAsync(token)));
        app.MapGet("/api/memory-protection", (HostMemoryProtectionService protection) => Results.Ok(protection.ReadStatus()));
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
