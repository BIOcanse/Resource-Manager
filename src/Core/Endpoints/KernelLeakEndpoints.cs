using ResourceManager.App.Infrastructure.KernelLeak;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapKernelLeakEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/kernel-leaks/history/clear", (KernelLeakObserver observer) =>
            Results.Ok(new ClearReportHistoryResponse(observer.ClearHistory())));
        return app;
    }

    public sealed record ClearReportHistoryResponse(int RemovedCount);
}
