using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Endpoints.Transport;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapHostManagerSmartRecordHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/optimization/smart/records/history", async (
            long? beforeSequence,
            int? limit,
            HttpResponse response,
            [Microsoft.AspNetCore.Mvc.FromServices] IHostManagerSmartRecordHistoryStore store,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            if (beforeSequence is <= 0)
                return Results.BadRequest(new EndpointErrorPayload("invalid-before-sequence"));
            if (limit is < 1 or > 500)
                return Results.BadRequest(new EndpointErrorPayload("invalid-history-limit"));

            var page = await store.ReadHistoryAsync(beforeSequence, limit ?? 100, cancellationToken);
            return Results.Ok(HostManagerSmartRecordHistoryWireProjection.Project(page));
        });

        app.MapPost("/api/optimization/smart/records/history/clear", async (
            [Microsoft.AspNetCore.Mvc.FromServices] IHostManagerSmartRecordHistoryStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(new HostManagerSmartRecordHistoryClearResponse(
                await store.ClearHistoryAsync(cancellationToken))));

        return app;
    }
}
