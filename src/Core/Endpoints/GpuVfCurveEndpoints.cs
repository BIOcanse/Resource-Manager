using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Endpoints.Transport;

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
                HttpContext context, string curveId, GpuVfCurveSaveRequest request, IGpuVfCurvePresets presets,
                GpuVfCurveHttpEditor httpEditor, CancellationToken token) =>
            {
                try { return Results.Ok(request.DraftId is null
                    ? await presets.SaveAsync(curveId, request, token)
                    : await httpEditor.SaveAsync(context, curveId, request, token)); }
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
                HttpContext context, GpuVfCurveEditorCommand command, GpuVfCurveHttpEditor editor, CancellationToken token) =>
            {
                try
                {
                    var draft = await editor.ExecuteAsync(context, command, token);
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
