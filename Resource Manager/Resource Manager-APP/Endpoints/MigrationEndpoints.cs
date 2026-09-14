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
using ResourceManager.App.Endpoints.Transport;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapMigrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/migrations/roots", (ISoftwareDataMigrationManager migrationManager) =>
        {
            return Results.Ok(migrationManager.GetRoots());
        }).AllowAnonymous();

        app.MapGet("/api/migrations/records", async (
            ISoftwareDataMigrationManager migrationManager,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await migrationManager.GetRecordsAsync(cancellationToken));
        }).AllowAnonymous();

        app.MapPost("/api/migrations/preview", (
            SoftwareDataMigrationRequest request,
            ISoftwareDataMigrationManager migrationManager) =>
        {
            return Results.Ok(migrationManager.Preview(request));
        });

        app.MapPost("/api/migrations/execute", async (
            SoftwareDataMigrationRequest request,
            IHostManagerOperationCommandService operationService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(HostManagerOperationWireProjection.Project(
                    await operationService.SubmitAsync(
                    HostManagerOperationRequestCodec.MigrationExecute(request),
                    cancellationToken)));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/migrations/restore", async (
            SoftwareDataRestoreRequest request,
            IHostManagerOperationCommandService operationService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(HostManagerOperationWireProjection.Project(
                    await operationService.SubmitAsync(
                    HostManagerOperationRequestCodec.MigrationRestore(request),
                    cancellationToken)));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/migrations/discovery/candidates", (
            SoftwareDataDiscoveryRequest request,
            ISoftwareDataDiscoveryManager discoveryManager) =>
        {
            return Results.Ok(discoveryManager.FindNameCandidates(request));
        });

        app.MapGet("/api/migrations/discovery/sessions", (ISoftwareDataDiscoveryManager discoveryManager) =>
        {
            return Results.Ok(discoveryManager.GetSessions());
        }).AllowAnonymous();

        app.MapPost("/api/migrations/discovery/start", async (
            SoftwareDataDiscoveryStartRequest request,
            IHostManagerOperationCommandService operationService,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(HostManagerOperationWireProjection.Project(
                await operationService.SubmitAsync(
                HostManagerOperationRequestCodec.DiscoveryStart(request),
                cancellationToken)));
        });

        app.MapPost("/api/migrations/discovery/{id}/stop", (
            string id,
            IHostManagerOperationCommandService operationService,
            CancellationToken cancellationToken) =>
        {
            return CancelDiscoveryAsync(
                operationService,
                id,
                cancellationToken);
        });

        return app;
    }

    private static async Task<IResult> CancelDiscoveryAsync(
        IHostManagerOperationCommandService operationService,
        string id,
        CancellationToken cancellationToken)
    {
        var operation = await operationService.CancelAsync(id, cancellationToken);
        return operation is null
            ? Results.NotFound()
            : Results.Ok(HostManagerOperationWireProjection.Project(operation));
    }
}
