using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.CpuMonopolyProtection;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static void MapCpuMonopolyProtectionEndpoints(this IEndpointRouteBuilder app, StartupCapabilitySet capabilities)
    {
        app.MapGet("/api/cpu-monopoly-protection", (CpuMonopolyProtectionService protection) => Results.Ok(protection.Read()));
        app.MapPost("/api/cpu-monopoly-protection/records/history/clear", (CpuMonopolyProtectionService protection)
            => Results.Ok(new ClearReportHistoryResponse(protection.ClearHistory())));
        if (capabilities.Allows(StartupCapability.MutablePersistence))
        app.MapPatch("/api/cpu-monopoly-protection", async (AppSettingsPatchRequest request,
            IRuntimeSpecializationCoordinator runtime, CancellationToken token) =>
        {
            if (request.Changes.ValueKind != System.Text.Json.JsonValueKind.Object
                || request.Changes.EnumerateObject().Count() != 1 || !request.Changes.TryGetProperty("cpuMonopolyProtection", out _))
                return Results.BadRequest(new EndpointErrorPayload("expected-cpu-monopoly-protection-settings-patch"));
            try
            {
                var result = await runtime.ApplyAppSettingsPatchAsync(request, "cpu-monopoly-protection-settings-patched", token);
                return result.RuntimeApplicationDisposition switch
                {
                    "committedAndApplied" or "committedWithCapabilityConstraints" => Results.Ok(result),
                    "committedWithDeliveryFailures" or "savedNotApplied" => Results.Json(result, statusCode: 202),
                    _ => Results.Json(result, statusCode: 409)
                };
            }
            catch (AppSettingsPatchException error) { return Results.BadRequest(new EndpointErrorPayload(error.Message)); }
        });
    }
}
