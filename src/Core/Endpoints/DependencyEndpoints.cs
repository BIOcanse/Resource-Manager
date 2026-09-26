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
    private static IEndpointRouteBuilder MapDependencyEndpoints(
        this IEndpointRouteBuilder app,
        StartupCapabilitySet startupCapabilities)
    {
        app.MapGet("/api/dependencies", async (
            IOptionalDependencyManager dependencyManager,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await dependencyManager.GetStatusesAsync(cancellationToken));
        }).AllowAnonymous();

        app.MapGet("/api/dependencies/{id}", async (
            string id,
            IOptionalDependencyManager dependencyManager,
            CancellationToken cancellationToken) =>
        {
            var status = await dependencyManager.GetStatusAsync(id, cancellationToken);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).AllowAnonymous();

        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            app.MapPost("/api/dependencies/{id}/download", async (
            string id,
            OptionalDependencyDownloadRequest request,
            IHostManagerOperationCommandService operationService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(HostManagerOperationWireProjection.Project(
                    await operationService.SubmitAsync(
                    HostManagerOperationRequestCodec.DependencyDownload(id, request),
                    cancellationToken)));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            });

            app.MapPost("/api/dependencies/{id}/launch-installer", async (
            string id,
            OptionalDependencyInstallRequest request,
            IHostManagerOperationCommandService operationService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(HostManagerOperationWireProjection.Project(
                    await operationService.SubmitAsync(
                    HostManagerOperationRequestCodec.DependencyInstaller(id, request),
                    cancellationToken)));
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
