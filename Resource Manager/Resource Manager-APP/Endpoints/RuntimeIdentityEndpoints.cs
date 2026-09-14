using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapRuntimeIdentityEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet(LoopbackSessionProofContract.EndpointPath, (
            string? challenge,
            [FromServices] LoopbackApiAccessToken accessToken) =>
        {
            if (!LoopbackSessionProofContract.IsValidChallenge(challenge))
            {
                return Results.BadRequest(new
                {
                    error = "invalid-loopback-session-challenge"
                });
            }

            return Results.Ok(new LoopbackSessionProofResponse(
                LoopbackSessionProofContract.ProductId,
                LoopbackSessionProofContract.ProtocolVersion,
                challenge!,
                LoopbackSessionProofContract.ComputeProof(
                    accessToken.Token,
                    challenge!)));
        }).AllowAnonymous();

        app.MapGet("/api/runtime/identity", (
            string? challenge,
            HostManagerRuntimeIdentity runtimeIdentity) =>
        {
            if (!BackendRuntimeIdentityContract.IsValidChallenge(challenge))
            {
                return Results.BadRequest(new
                {
                    error = "invalid-runtime-identity-challenge"
                });
            }

            return Results.Ok(new BackendRuntimeIdentityResponse(
                BackendRuntimeIdentityContract.ProductId,
                BackendRuntimeIdentityContract.ProtocolVersion,
                ResolveBuildVersion(),
                runtimeIdentity.InstanceId,
                runtimeIdentity.ProcessId,
                runtimeIdentity.ProcessCreatedUtcTicks,
                Path.GetFullPath(
                    Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? throw new InvalidOperationException(
                        "The backend executable path is unavailable.")),
                challenge!));
        }).AllowAnonymous();

        app.MapGet("/api/runtime/capabilities", (
            StartupCapabilitySet capabilities) =>
            Results.Ok(new BackendStartupCapabilitiesResponse(
                capabilities.ProfileId,
                capabilities.Allowed == StartupCapability.None,
                capabilities.Allows(StartupCapability.MutablePersistence),
                capabilities.Allows(StartupCapability.LegacyPersistenceImport),
                capabilities.Allows(
                    StartupCapability.GpuLaunchInterceptionReconciliation),
                capabilities.Allows(StartupCapability.RuntimeEffectOwners),
                capabilities.Allows(StartupCapability.PublicServiceCoordination),
                capabilities.Allows(StartupCapability.OptimizationRuntime),
                capabilities.Allows(StartupCapability.SharedResourceOwnership)))).AllowAnonymous();

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
}
