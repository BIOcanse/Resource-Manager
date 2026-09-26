using ResourceManager.App.Infrastructure.Security;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapOptimizationEndpoints(
        this IEndpointRouteBuilder app,
        LoopbackApiPipelineMode loopbackApiPipelineMode)
    {
        app.MapOptimizationReportEndpoints();
        app.MapHostManagerSmartCoordinatorEndpoints(loopbackApiPipelineMode);

        return app;
    }
}
