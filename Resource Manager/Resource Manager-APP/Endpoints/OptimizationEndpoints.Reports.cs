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
            return trust is null ? Results.NotFound(new { error = "报告不存在或已经失效。" }) : Results.Ok(trust);
        });

        app.MapPost("/api/optimization/reports/{id}/trust", async (
            string id,
            IHostManagerReportService reportService,
            CancellationToken cancellationToken) =>
        {
            var trust = await reportService.TrustReportAsync(id, cancellationToken);
            return trust is null ? Results.NotFound(new { error = "报告不存在或已经失效。" }) : Results.Ok(trust);
        });

        app.MapPost("/api/optimization/reports/{id}/protect", async (
            string id,
            IHostManagerReportService reportService,
            IOptimizationProtectionService protectionService,
            CancellationToken cancellationToken) =>
        {
            var report = await reportService.GetReportAsync(id, cancellationToken);
            var protectedTarget = report is null
                ? null
                : await protectionService.ProtectReportAsync(
                    report,
                    cancellationToken);
            return protectedTarget is null ? Results.NotFound(new { error = "报告不存在、已经失效，或目标不是软件。" }) : Results.Ok(protectedTarget);
        });

        app.MapGet("/api/optimization/status", (HttpResponse response, IHostManagerReportService reportService) =>
        {
            DisableResponseCache(response);
            return Results.Ok(reportService.GetStatus());
        }).AllowAnonymous();

        return app;
    }
}
