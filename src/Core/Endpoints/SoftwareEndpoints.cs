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
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Application.SoftwareIssues;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.SoftwareDiscovery;
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
using ResourceManager.App.Endpoints.Transport;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapSoftwareEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/software", async (
            bool? refresh,
            ISoftwareRegistryView softwareRegistry,
            ISoftwareIssueProjection softwareIssueProjection,
            IPortableSoftwareDiscovery portableSoftwareDiscovery,
            CancellationToken cancellationToken) =>
        {
            if (refresh == true)
            {
                await portableSoftwareDiscovery.ScanRunningProcessesAsync(cancellationToken);
            }

            var records = refresh == true
                ? await softwareRegistry.RefreshSoftwareAsync(cancellationToken)
                : await softwareRegistry.GetSoftwareAsync(cancellationToken);
            return Results.Ok(await softwareIssueProjection.ProjectAsync(
                records,
                cancellationToken));
        }).AllowAnonymous();

        app.MapPost("/api/software/portable/root", async (
            PortableSoftwareRootConfirmationRequest request,
            IPortableSoftwareRegistry portableSoftwareRegistry,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await portableSoftwareRegistry.ConfirmRootPathAsync(request, cancellationToken);
                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        app.MapGet("/api/software/manual", async (
            IManualSoftwareRegistry manualSoftwareRegistry,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await manualSoftwareRegistry.GetAllAsync(cancellationToken));
        }).AllowAnonymous();

        app.MapPost("/api/software/manual", async (
            ManualSoftwareRequest request,
            IManualSoftwareRegistry manualSoftwareRegistry,
            IRuntimeProcessAttributionCatalogProvider processAttributionCatalogProvider,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var record = await manualSoftwareRegistry.AddOrUpdateAsync(request, cancellationToken);
                await processAttributionCatalogProvider.InvalidateSoftwareSnapshotAsync();
                return Results.Ok(record);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/software/manual/{id}", async (
            string id,
            IManualSoftwareRegistry manualSoftwareRegistry,
            IRuntimeProcessAttributionCatalogProvider processAttributionCatalogProvider,
            CancellationToken cancellationToken) =>
        {
            if (!await manualSoftwareRegistry.RemoveAsync(id, cancellationToken))
            {
                return Results.NotFound(new { error = "手动软件记录不存在。" });
            }

            await processAttributionCatalogProvider.InvalidateSoftwareSnapshotAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/software/uninstall", async (
            SoftwareOperationRequest request,
            IHostManagerOperationCommandService operationService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(HostManagerOperationWireProjection.Project(
                    await operationService.SubmitAsync(
                    HostManagerOperationRequestCodec.SoftwareUninstall(request),
                    cancellationToken)));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }
}
