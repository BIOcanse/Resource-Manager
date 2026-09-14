using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Application.Migration;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Domain.Migration;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ResourceTable;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Endpoints.Transport;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapComponentEndpoints(
        this IEndpointRouteBuilder app,
        StartupCapabilitySet startupCapabilities)
    {
        app.MapGet("/api/components", async (
            IComponentManager componentManager,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await componentManager.GetStatusesAsync(cancellationToken));
        }).AllowAnonymous();

        app.MapGet("/api/components/{id}", async (
            string id,
            IComponentManager componentManager,
            CancellationToken cancellationToken) =>
        {
            var status = await componentManager.GetStatusAsync(id, cancellationToken);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).AllowAnonymous();

        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            app.MapPost("/api/components/{id}/download", async (
            string id,
            ComponentActionRequest request,
            IHostManagerOperationCommandService operationService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(HostManagerOperationWireProjection.Project(
                    await operationService.SubmitAsync(
                    HostManagerOperationRequestCodec.ComponentDownload(id, request),
                    cancellationToken)));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            });

            app.MapPost("/api/components/{id}/install", async (
            string id,
            ComponentActionRequest request,
            IHostManagerOperationCommandService operationService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(HostManagerOperationWireProjection.Project(
                    await operationService.SubmitAsync(
                    HostManagerOperationRequestCodec.ComponentInstall(id, request),
                    cancellationToken)));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            });

            app.MapPost("/api/components/{id}/verify", async (
            string id,
            IComponentManager componentManager,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await componentManager.VerifyAsync(id, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            });
        }

        return app;
    }
}
