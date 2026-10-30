using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapRuntimeIdentityEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => Results.NoContent()).AllowAnonymous();
        app.MapPost(InteractiveDesktopObservation.PublisherStreamPath, RunDesktopPublisherStreamAsync);
        // Legacy, no product caller since the publisher stream. Delete together with the POST below
        // once the user has reviewed .local/reports/desktop-publisher-stream.deletion-list.md.
        app.MapPost(InteractiveDesktopObservation.EndpointPath + "/publisher/select", (
            HttpContext context,
            ITrustedAccessKeyService keys,
            ResourceManager.App.Infrastructure.ResourceBreakdown.InteractiveDesktopObservationStore desktops) =>
        {
            if (!WindowsLoopbackApiAdministratorReader.TryReadCallerIdentity(context, out var caller)
                || caller.WindowsSessionId <= 0)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var lifetime = context.Features.Get<IConnectionLifetimeFeature>();
            if (lifetime is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return desktops.SelectPublisher(caller.WindowsSessionId, context.Connection.Id, caller.ProcessId,
                    new DateTime(caller.ProcessCreatedUtcTicks, DateTimeKind.Utc).ToFileTimeUtc(),
                    lifetime.ConnectionClosed,
                    ReadAuthorization(context)?.Key is { } key
                        ? keys.GetRevocationToken(key.Id) : CancellationToken.None)
                ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict);
        });
        // Legacy, no product caller since the publisher stream. Delete together with the select above
        // once the user has reviewed .local/reports/desktop-publisher-stream.deletion-list.md.
        app.MapPost(InteractiveDesktopObservation.EndpointPath, (
            InteractiveDesktopObservation observation,
            HttpContext context,
            ResourceManager.App.Infrastructure.ResourceBreakdown.InteractiveDesktopObservationStore desktops) =>
            desktops.Publish(observation, context.Connection.Id) ? Results.Ok() : Results.BadRequest(new EndpointErrorPayload("invalid-desktop-observation")))
            .WithMetadata(new RequestSizeLimitAttribute(InteractiveDesktopObservation.MaximumRequestBytes));

        app.MapGet("/api/runtime/identity", (
            string? challenge,
            HostManagerRuntimeIdentity runtimeIdentity) =>
        {
            if (!BackendRuntimeIdentityContract.IsValidChallenge(challenge))
            {
                return Results.BadRequest(new EndpointErrorPayload("invalid-runtime-identity-challenge"));
            }

            return Results.Ok(new BackendRuntimeIdentityResponse(
                BackendRuntimeIdentityContract.ProductId,
                BackendRuntimeIdentityContract.ProtocolVersion,
                ResolveBuildVersion(),
                runtimeIdentity.InstanceId,
                runtimeIdentity.ProcessId,
                runtimeIdentity.ProcessCreatedUtcTicks,
                ResolveProcessImagePath(),
                challenge!));
        }).AllowAnonymous();

        app.MapGet("/api/runtime/capabilities", (
            StartupCapabilitySet capabilities) =>
            Results.Ok(new BackendStartupCapabilitiesResponse(
                capabilities.ProfileId,
                capabilities.Allowed == StartupCapability.None,
                capabilities.Allows(StartupCapability.MutablePersistence),
                capabilities.Allows(
                    StartupCapability.GpuLaunchInterceptionReconciliation),
                capabilities.Allows(StartupCapability.RuntimeEffectOwners),
                capabilities.Allows(StartupCapability.PublicServiceCoordination),
                capabilities.Allows(StartupCapability.OptimizationRuntime)))).AllowAnonymous();

        return app;
    }

    // The publisher stream. The caller's process becomes the selected publisher of its Windows session
    // for as long as this request lasts; each line of the body is one observation and replaces the
    // session's observation. The selection, and the observation with it, is released when the body
    // ends, the request is aborted, the key it was selected with is revoked, or a line is invalid.
    // The request is chunked application/x-ndjson, one line per chunk; its terminating chunk is the
    // publisher's normal stop. Nothing is bound from the body (every parameter comes from services),
    // so no content type is required or refused; the body is read only from Request.BodyReader.
    // A 200 carries no Content-Length and no data: the headers are flushed at once and the response
    // ends when the stream does.
    private static async Task RunDesktopPublisherStreamAsync(
        HttpContext context,
        [FromServices] ITrustedAccessKeyService keys,
        [FromServices] ResourceManager.App.Infrastructure.ResourceBreakdown.InteractiveDesktopObservationStore desktops,
        [FromServices] IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json)
    {
        // Both refusals end the request at once without reading the body.
        if (!WindowsLoopbackApiAdministratorReader.TryReadCallerIdentity(context, out var caller)
            || caller.WindowsSessionId <= 0)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var revoked = ReadAuthorization(context)?.Key is { } key
            ? keys.GetRevocationToken(key.Id) : CancellationToken.None;
        // The selection's lifetime: this request. It ends on abort, or below when the stream ends.
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var publisherKey = "stream:" + Guid.NewGuid().ToString("N");
        if (!desktops.SelectPublisher(caller.WindowsSessionId, publisherKey, caller.ProcessId,
                new DateTime(caller.ProcessCreatedUtcTicks, DateTimeKind.Utc).ToFileTimeUtc(),
                stream.Token, revoked))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(stream.Token, revoked);
        try
        {
            // The publisher writes only when the desktop changes, so the body may stay silent for
            // as long as the stream lasts and has no total length.
            if (context.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate) rate.MinDataRate = null;
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                size.MaxRequestBodySize = null;
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.StartAsync(reading.Token);
            var observation = (JsonTypeInfo<InteractiveDesktopObservation>)json.Value.SerializerOptions
                .GetTypeInfo(typeof(InteractiveDesktopObservation));
            await ReadDesktopObservationLinesAsync(context.Request.BodyReader, observation,
                sample => desktops.Publish(sample, publisherKey), reading.Token);
        }
        // The terminating chunk returns normally above. Abort, revocation, a reset connection or a
        // body cut off mid-chunk: each one also only ends the stream and is not reported as an error.
        catch (Exception error) when (error is OperationCanceledException or IOException
            or Microsoft.AspNetCore.Http.BadHttpRequestException) { }
        finally
        {
            // Releases the selection and removes the session's observation (the store's release path).
            stream.Cancel();
        }
    }

    // Reads lines until the body ends. Each line must be one observation the store accepts; an
    // unreadable, oversized or rejected line ends the stream instead of being skipped.
    private static async Task ReadDesktopObservationLinesAsync(PipeReader body,
        JsonTypeInfo<InteractiveDesktopObservation> observation,
        Func<InteractiveDesktopObservation, bool> publish, CancellationToken cancellationToken)
    {
        while (true)
        {
            var read = await body.ReadAsync(cancellationToken);
            var buffer = read.Buffer;
            var valid = true;
            while (valid && buffer.PositionOf((byte)'\n') is { } end)
            {
                var line = buffer.Slice(0, end);
                valid = line.Length <= InteractiveDesktopObservation.MaximumRequestBytes
                    && ReadDesktopObservationLine(line, observation) is { } sample && publish(sample);
                buffer = buffer.Slice(buffer.GetPosition(1, end));
            }
            // An unterminated remainder longer than the line limit can never become a valid line.
            valid &= buffer.Length <= InteractiveDesktopObservation.MaximumRequestBytes;
            body.AdvanceTo(buffer.Start, buffer.End);
            if (!valid || read.IsCompleted || read.IsCanceled) return;
        }
    }

    private static InteractiveDesktopObservation? ReadDesktopObservationLine(ReadOnlySequence<byte> line,
        JsonTypeInfo<InteractiveDesktopObservation> observation)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            var sample = JsonSerializer.Deserialize(ref reader, observation);
            // Exactly one JSON value per line.
            return reader.Read() ? null : sample;
        }
        catch (JsonException) { return null; }
    }

    private static string ResolveBuildVersion()
    {
        var assembly = Assembly.GetEntryAssembly()
            ?? typeof(ResourceManagerEndpointRouteBuilderExtensions).Assembly;
        return assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static string ResolveProcessImagePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);
        using var process = Process.GetCurrentProcess();
        return Path.GetFullPath(WindowsProcessImagePathReader.TryRead(process)
            ?? throw new InvalidOperationException("The backend executable path is unavailable."));
    }
}
