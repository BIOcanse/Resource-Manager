using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.LocalSystem;
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
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Endpoints.Transport;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapSoftwareEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/software", async (
            bool? refresh,
            HttpContext context,
            ISoftwareRegistryView softwareRegistry,
            ISoftwareIssueProjection softwareIssueProjection,
            IRuntimeSoftwareObservationSource runtimeSoftware,
            IPortableSoftwareDiscovery portableSoftwareDiscovery,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadInteractiveCaller(context, out var caller))
                return Results.Json(new EndpointCodePayload("InteractiveUserSessionRequired"), statusCode: StatusCodes.Status403Forbidden);
            if (refresh == true)
            {
                await portableSoftwareDiscovery.ScanRunningProcessesAsync(cancellationToken);
            }

            var records = refresh == true
                ? await softwareRegistry.RefreshSoftwareAsync(caller.WindowsSid, cancellationToken, runtimeSoftware.ReadLatestSoftware())
                : await softwareRegistry.GetSoftwareAsync(caller.WindowsSid, cancellationToken, runtimeSoftware.ReadLatestSoftware());
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
                return Results.BadRequest(new EndpointMessagePayload(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new EndpointMessagePayload(ex.Message));
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
                return Results.BadRequest(new EndpointErrorPayload(ex.Message));
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
                return Results.NotFound(new EndpointErrorPayload("手动软件记录不存在。"));
            }

            await processAttributionCatalogProvider.InvalidateSoftwareSnapshotAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/software/uninstall", async (
            SoftwareOperationRequest request,
            HttpContext context,
            IHostManagerOperationCommandService operationService,
            ISoftwareRegistryView softwareRegistry,
            CancellationToken cancellationToken) =>
        {
            if (!ResourceManager.App.Infrastructure.Security.WindowsLoopbackApiAdministratorReader.TryReadCallerIdentity(context, out var caller)
                || caller.ProcessId == Environment.ProcessId
                || caller.WindowsSessionId <= 0
                || (request.Id.StartsWith("dependency:", StringComparison.OrdinalIgnoreCase) && !caller.IsAdministrator))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                return Results.Ok(HostManagerOperationWireProjection.Project(
                    await operationService.SubmitAsync(
                    await CreateSoftwareUninstallCommandAsync(request,
                        new(caller.WindowsSid, checked((uint)caller.WindowsSessionId)), softwareRegistry,
                        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OperationTargetCapture"),
                        cancellationToken),
                    cancellationToken)));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new EndpointErrorPayload(ex.Message));
            }
        });

        return app;
    }

    internal static async Task<ResourceManager.App.Domain.Operations.HostManagerOperationSubmitCommand> CreateSoftwareUninstallCommandAsync(
        SoftwareOperationRequest request, SoftwareOperationCaller caller, ISoftwareRegistryView registry,
        ILogger logger, CancellationToken cancellationToken)
    {
        string? name = null;
        try
        {
            var records = await registry.GetSoftwareAsync(caller.WindowsSid, cancellationToken);
            name = records.FirstOrDefault(record =>
                string.Equals(record.Id, request.Id, StringComparison.OrdinalIgnoreCase))?.Name;
            if (name is null)
                logger.LogInformation("No software name was recorded for operation target {TargetId} in the caller's catalog.", request.Id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(error, "The software catalog was unavailable when accepting operation target {TargetId}; its recorded name is null.", request.Id);
        }
        return HostManagerOperationRequestCodec.SoftwareUninstall(request, caller, name);
    }
}
