using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.Security;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapCpuLeaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/cpu/leases", async (RuntimeCpuLeaseRequest request, HttpContext context,
            IRuntimeCpuLeaseService leases, IOptions<JsonOptions> json, CancellationToken cancellationToken) =>
        {
            var authorization = ReadAuthorization(context);
            if (authorization is null) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            // This is the same disconnect and trusted-key revocation lifetime as /api/subscriptions/stream.
            CancellationToken revocation;
            try
            {
                revocation = authorization.Key is { } key
                    ? context.RequestServices.GetRequiredService<ITrustedAccessKeyService>().GetRevocationToken(key.Id)
                    : CancellationToken.None;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new EndpointErrorPayload("access-service-unavailable"), cancellationToken);
                return;
            }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revocation);
            RuntimeCpuLeaseSnapshot lease;
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                lease = leases.CreateCpuLease(request, authorization.Key is { } holderKey
                    ? "access-key:" + holderKey.Id : "windows-request:" + context.TraceIdentifier);
            }
            catch (InvalidOperationException error)
            {
                context.Response.StatusCode = error.Message == "cpu-topology-unavailable" ? StatusCodes.Status503ServiceUnavailable
                    : error.Message == "cpu-lease-capacity-exceeded" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new EndpointErrorPayload(error.Message), cancellationToken);
                return;
            }
            try
            {
                NdjsonResponse.Prepare(context.Response);
                await NdjsonResponse.WriteAsync(context.Response, lease, json.Value.SerializerOptions, lifetime.Token);
                await leases.WaitForCpuLeaseEndAsync(lease.LeaseId, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally { leases.DeleteCpuLease(lease.LeaseId); }
        });

        app.MapGet("/api/cpu/leases/{leaseId}", async (string leaseId, HttpResponse response,
            IRuntimeCpuLeaseService leases, CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var lease = await leases.ReadCpuLeaseAsync(leaseId, cancellationToken);
            return lease is null ? Results.NotFound(new EndpointErrorPayload("cpu-lease-not-found")) : Results.Ok(lease);
        });
        app.MapDelete("/api/cpu/leases/{leaseId}", (string leaseId, IRuntimeCpuLeaseService leases)
            => leases.DeleteCpuLease(leaseId) ? Results.NoContent() : Results.NotFound(new EndpointErrorPayload("cpu-lease-not-found")));
        return app;
    }
}
