using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Security;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using ResourceManager.App.Endpoints.Transport;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapHostManagerSmartCoordinatorEndpoints(
        this IEndpointRouteBuilder app,
        LoopbackApiPipelineMode loopbackApiPipelineMode)
    {
        app.MapGet("/api/optimization/smart/status", async (
            HttpResponse response,
            IHostManagerSmartCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await coordinator.GetStatusAsync(cancellationToken));
        }).AllowAnonymous();

        app.MapGet("/api/optimization/smart/state", async (
            HttpResponse response,
            IHostManagerSmartCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await coordinator.GetStateAsync(cancellationToken));
        }).AllowAnonymous();

        app.MapPost("/api/optimization/smart/mode", async (
            HostManagerSmartCoordinatorModeRequest request,
            IHostManagerSmartCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await coordinator.SetModeAsync(request.Mode, cancellationToken));
        }).AllowAnonymous();

        app.MapPost("/api/optimization/smart/run-once", async (
            IHostManagerSmartCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await coordinator.RunOnceAsync(cancellationToken));
        }).AllowAnonymous();

        if (loopbackApiPipelineMode == LoopbackApiPipelineMode.Administrator)
        {
            app.MapGet("/api/optimization/smart/diagnostics/decision-snapshot/subscribe", async (
                HttpResponse response,
                HostManagerSmartCoordinator coordinator,
                IOptions<JsonOptions> jsonOptions,
                CancellationToken cancellationToken) =>
            {
                NdjsonResponse.Prepare(response);
                using var writer = new FrontendSubscriptionChannelWriter(response, jsonOptions.Value.SerializerOptions);
                try
                {
                    await PublishAlignedPeriodicAsyncSubscriptionAsync(
                        "decision-snapshot", coordinator.GetDecisionDiagnosticsAsync,
                        TimeSpan.FromSeconds(5), writer, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            });

            app.MapGet("/api/optimization/smart/diagnostics/decision-snapshot", async (
                HttpResponse response,
                HostManagerSmartCoordinator coordinator,
                CancellationToken cancellationToken) =>
            {
                DisableResponseCache(response);
                return Results.Ok(await coordinator.GetDecisionDiagnosticsAsync(
                    cancellationToken));
            });

            app.MapGet("/api/optimization/smart/validation/process-effect-scope", async (
                HttpResponse response,
                IHostManagerProcessEffectValidationScopeControl control,
                CancellationToken cancellationToken) =>
            {
                DisableResponseCache(response);
                return Results.Ok(await control.GetProcessEffectValidationScopeAsync(cancellationToken));
            });

            app.MapPost("/api/optimization/smart/validation/process-effect-scope/open", async (
                HostManagerProcessEffectValidationScopeOpenRequest request,
                IHostManagerProcessEffectValidationScopeControl control,
                CancellationToken cancellationToken) =>
            {
                return Results.Ok(await control.OpenProcessEffectValidationScopeAsync(
                    request,
                    cancellationToken));
            });

            app.MapPost("/api/optimization/smart/validation/process-effect-scope/close", async (
                HostManagerProcessEffectValidationScopeCloseRequest request,
                IHostManagerProcessEffectValidationScopeControl control,
                CancellationToken cancellationToken) =>
            {
                return Results.Ok(await control.CloseProcessEffectValidationScopeAsync(
                    request,
                    cancellationToken));
            });

            app.MapGet("/api/optimization/smart/validation/memory-cleanup-evidence", async (
                HttpResponse response,
                IHostManagerMemoryCleanupValidationEvidenceControl control,
                CancellationToken cancellationToken) =>
            {
                DisableResponseCache(response);
                return Results.Ok(await control.GetMemoryCleanupValidationEvidenceAsync(
                    cancellationToken));
            });
        }

        return app;
    }
}
