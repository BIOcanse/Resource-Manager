using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapFrontendRuntimeStateEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/adapters/resource-manager/scheduling", (
            IResourceManagerSelfSchedulingControl schedulingControl) =>
        {
            return Results.Ok(schedulingControl.GetSchedulingSnapshot());
        }).AllowAnonymous();

        // This input updates only this process's transient self-scheduling state.
        app.MapPost("/api/adapters/resource-manager/scheduling", (
            AdapterSoftwareSchedulingEnvelope envelope,
            IResourceManagerSelfSchedulingControl schedulingControl) =>
        {
            return Results.Ok(schedulingControl.ApplyScheduling(envelope));
        });

        return app;
    }
}
