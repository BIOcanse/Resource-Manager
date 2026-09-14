using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static void MapCpuBaselineRatioEndpoints(
        this IEndpointRouteBuilder app,
        StartupCapabilitySet startupCapabilities)
    {
        app.MapGet("/api/cpu/baseline-ratio", async (
            HttpResponse response,
            IRuntimePlanProvider plans,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            await response.WriteAsJsonAsync(plans.Current.CpuBaseline, cancellationToken);
        }).AllowAnonymous();

        if (!startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners)) return;

        app.MapPut("/api/cpu/baseline-ratio", (
            CpuBaselineRatioRequest request,
            IRuntimeSpecializationCoordinator coordinator,
            CancellationToken cancellationToken) =>
            ApplyCpuBaselineRatioAsync(coordinator, request.Ratio, cancellationToken));
        app.MapDelete("/api/cpu/baseline-ratio", (
            IRuntimeSpecializationCoordinator coordinator,
            CancellationToken cancellationToken) =>
            ApplyCpuBaselineRatioAsync(coordinator, null, cancellationToken));
    }

    private static async Task<IResult> ApplyCpuBaselineRatioAsync(
        IRuntimeSpecializationCoordinator coordinator,
        double? overrideRatio,
        CancellationToken cancellationToken)
    {
        if (overrideRatio.HasValue && !CpuBaselineRatio.IsValid(overrideRatio.Value))
        {
            return Results.BadRequest(new { error = "CPU baseline ratio must be greater than zero and at most one, with a finite reciprocal." });
        }
        var result = await coordinator.ApplyCpuBaselineRatioAsync(overrideRatio, cancellationToken);
        return result.RuntimeApplicationDisposition == "applied"
            ? Results.Ok(result)
            : Results.Conflict(result);
    }
}
