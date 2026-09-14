using ResourceManager.App.Application.Diagnostics;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapDebugEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/debug/freedom-points", (
            HttpResponse response,
            IRuntimePlanProvider runtimePlanProvider) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(runtimePlanProvider.Current.HostManager.FreedomPoints);
        });

        app.MapGet("/api/debug/logs", (
            int? tail,
            HttpContext context,
            IDebugDiagnosticLogReader logReader) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                ok = true,
                lines = logReader.ReadTail(tail ?? 100)
            });
        });

        app.MapGet("/api/debug/history/requirements", (
            HttpResponse response,
            IRuntimePlanProvider runtimePlanProvider) =>
        {
            response.Headers.CacheControl = "no-store";
            var plan = runtimePlanProvider.Current.HostManager;
            return Results.Ok(new
            {
                requirements = plan.DataHistory.Requirements,
                consumers = new[]
                {
                    new
                    {
                        module = plan.SmartCoordinator.Build.NativeModule,
                        requirements = plan.SmartCoordinator.DataHistory.Requirements
                    }
                }
            });
        });

        app.MapGet("/api/debug/metrics/capture", async (
            HttpRequest request,
            HttpResponse response,
            IMetricSampler sampler,
            CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var sampleRequest = request.Query.TryGetValue("ids", out var ids)
                ? MetricSampleRequest.ForIds(ids)
                : MetricSampleRequest.All;
            var snapshot = await sampler.CaptureSnapshotAsync(
                sampleRequest,
                cancellationToken);
            return Results.Ok(snapshot);
        });

        return app;
    }
}
