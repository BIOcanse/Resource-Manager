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

        app.MapPost("/api/optimization/reports/history/clear", async (
            IHostManagerReportService reportService,
            CancellationToken cancellationToken) =>
        {
            var removedCount = await reportService.ClearHistoryAsync(cancellationToken);
            return Results.Ok(new OptimizationReportHistoryClearReply(removedCount));
        });

        app.MapPost("/api/optimization/reports/{id}/dismiss", async (
            string id,
            IHostManagerReportService reportService,
            CancellationToken cancellationToken) =>
        {
            var trust = await reportService.DismissReportAsync(id, cancellationToken);
            return trust is null ? Results.NotFound(new EndpointErrorPayload("optimization-report-not-found")) : Results.Ok(trust);
        });

        app.MapDelete("/api/optimization/trust/{id}", async (
            string id,
            IHostManagerReportService reportService,
            CancellationToken cancellationToken) =>
        {
            return await reportService.RemoveTrustedTargetAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound(new EndpointErrorPayload("optimization-trust-not-found"));
        });

        return app;
    }
}

public sealed record OptimizationReportHistoryClearReply(int RemovedCount);
