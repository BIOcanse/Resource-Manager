using ResourceManager.App.Application.Optimization;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapOptimizationReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/optimization/reports", async (
            HttpResponse response,
            IHostManagerReportService reportService,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await reportService.GetReportsAsync(cancellationToken));
        }).AllowAnonymous();

        app.MapPost("/api/optimization/reports/refresh", async (
            HttpResponse response,
            IHostManagerReportService reportService,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await reportService.RefreshReportsAsync(cancellationToken));
        });

        app.MapPost("/api/optimization/reports/{id}/dismiss", async (
            string id,
            IHostManagerReportService reportService,
            CancellationToken cancellationToken) =>
        {
            var trust = await reportService.DismissReportAsync(id, cancellationToken);
            return trust is null ? Results.NotFound(new EndpointErrorPayload("报告不存在或已经失效。")) : Results.Ok(trust);
        });

        app.MapDelete("/api/optimization/trust/{id}", async (
            string id,
            IHostManagerReportService reportService,
            CancellationToken cancellationToken) =>
        {
            return await reportService.RemoveTrustedTargetAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound(new EndpointErrorPayload("信任项不存在。"));
        });

        return app;
    }
}
