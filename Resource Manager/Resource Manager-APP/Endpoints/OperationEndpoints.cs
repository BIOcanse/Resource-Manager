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
using ResourceManager.App.Endpoints.Transport;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapOperationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/operations", (IHostManagerOperationQueryService operations) =>
        {
            return Results.Ok(HostManagerOperationWireProjection.Project(
                operations.GetPublishedState()));
        }).AllowAnonymous();

        app.MapGet("/api/operations/{id}", (
            string id,
            IHostManagerOperationQueryService operations) =>
        {
            var operation = operations.Get(id);
            return operation is null
                ? Results.NotFound(new { error = $"操作不存在或已经失效：{id}" })
                : Results.Ok(HostManagerOperationWireProjection.Project(operation));
        }).AllowAnonymous();

        app.MapPost("/api/operations/{id}/cancel", async (
            string id,
            IHostManagerOperationCommandService operations,
            CancellationToken cancellationToken) =>
        {
            var operation = await operations.CancelAsync(id, cancellationToken);
            return operation is null
                ? Results.NotFound(new { error = $"操作不存在或已经失效：{id}" })
                : Results.Ok(HostManagerOperationWireProjection.Project(operation));
        });

        return app;
    }
}
