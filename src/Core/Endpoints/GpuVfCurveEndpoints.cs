using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Endpoints;

public sealed record GpuVfCurveReferencesPayload(IReadOnlyList<GpuVfCurveReference> References);
public sealed record GpuVfCurveDeletedPayload(bool Deleted);
public sealed record GpuVfCurveClosedPayload(bool Closed);
public sealed record GpuVfCurveErrorPayload(ControlApplyReason Reason, IReadOnlyList<GpuVfCurveReference> References);

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapGpuVfCurveEndpoints(this IEndpointRouteBuilder app,
        StartupCapabilitySet startupCapabilities)
    {
        app.MapGet("/api/control/objects/{objectId}/gpu-vf-curve", async (
            string objectId, HttpResponse response, IGpuVfCurveDescriptorProvider provider, CancellationToken token) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await provider.GetAsync(objectId, token));
        }).AllowAnonymous();
        app.MapGet("/api/control/gpu-vf-curves", async (
            string? objectId, HttpResponse response, IGpuVfCurvePresets presets, CancellationToken token) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await presets.ReadAsync(objectId, token));
        }).AllowAnonymous();
        app.MapGet("/api/control/gpu-vf-curves/{curveId}/references", async (
            string curveId, HttpResponse response, IGpuVfCurvePresets presets, CancellationToken token) =>
        {
            DisableResponseCache(response);
            return Results.Ok(new GpuVfCurveReferencesPayload(await presets.ReferencesAsync(curveId, token)));
        }).AllowAnonymous();
        // These operations only edit backend-owned user data, never hardware.
        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
        {
            app.MapPut("/api/control/gpu-vf-curves/{curveId}", async (
                string curveId, GpuVfCurveSaveRequest request, IGpuVfCurvePresets presets, CancellationToken token) =>
            {
                try { return Results.Ok(await presets.SaveAsync(curveId, request, token)); }
                catch (GpuVfCurveRequestException error) { return GpuVfCurveFailure(error); }
            });
            app.MapDelete("/api/control/gpu-vf-curves/{curveId}", async (
                string curveId, IGpuVfCurvePresets presets, CancellationToken token) =>
            {
                try
                {
                    await presets.DeleteAsync(curveId, token);
                    return Results.Ok(new GpuVfCurveDeletedPayload(true));
                }
                catch (GpuVfCurveRequestException error) { return GpuVfCurveFailure(error); }
            });
            app.MapPost("/api/control/gpu-vf-curves/editor/command", async (
                GpuVfCurveEditorCommand command, IGpuVfCurveEditor editor, CancellationToken token) =>
            {
                try
                {
                    var draft = await editor.ExecuteAsync(command, token);
                    return draft is null ? (IResult)Results.Ok(new GpuVfCurveClosedPayload(true)) : Results.Ok(draft);
                }
                catch (GpuVfCurveRequestException error) { return GpuVfCurveFailure(error); }
            });
        }
        return app;
    }

    private static IResult GpuVfCurveFailure(GpuVfCurveRequestException error)
        => Results.Json(new GpuVfCurveErrorPayload(error.Reason, error.References),
            statusCode: error.References.Count == 0 ? StatusCodes.Status400BadRequest : StatusCodes.Status409Conflict);
}
