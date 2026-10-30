using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Connections.Features;
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
