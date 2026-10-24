using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ResourceTable;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapGpuPlacementEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/gpu-placement/software/{softwareId}", async (
            string softwareId,
            string? softwareName,
            string? softwareKind,
            IGpuPlacementPolicyStore policyStore,
            IGpuPlacementProcessHistoryStore processHistoryStore,
            IGpuLaunchInterceptionRegistry launchInterceptionRegistry,
            IGpuLaunchExecutionReportStore launchExecutionReportStore,
            IGpuPlacementCapabilityReader capabilityReader,
            IMetricSampler metricSampler,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var resolvedName = string.IsNullOrWhiteSpace(softwareName) ? softwareId : softwareName.Trim();
                var softwarePolicy = await policyStore.GetOrCreateSoftwarePolicyAsync(
                    softwareId,
                    resolvedName,
                    softwareKind,
                    cancellationToken);
                var policyDocument = await policyStore.GetAsync(cancellationToken);
                var processPolicies = policyDocument.ProcessPolicies
                    .Where(policy => policy.SoftwareId.Equals(softwarePolicy.SoftwareId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(static policy => policy.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static policy => policy.ProcessKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var processHistory = await processHistoryStore.GetSoftwareHistoryAsync(
                    softwarePolicy.SoftwareId,
                    softwarePolicy.SoftwareName,
                    cancellationToken);
                var recentLaunches = await launchExecutionReportStore.GetLatestAsync(cancellationToken);
                var startupInterceptions = processPolicies.Select(policy =>
                {
                    var recent = recentLaunches.FirstOrDefault(report => string.Equals(
                        report.ExecutablePath,
                        policy.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase));
                    return launchInterceptionRegistry.GetStatus(policy) with
                    {
                        RecentLaunchResult = recent
                    };
                }).ToArray();
                var capabilityInputs = processHistory.Processes
                    .Select(process => new
                    {
                        process.ProcessKey,
                        process.ExecutablePath,
                        process.Architecture,
                        process.GraphicsApi
                    })
                    .Concat(processPolicies
                        .Where(policy => processHistory.Processes.All(process => !process.ProcessKey.Equals(
                            policy.ProcessKey,
                            StringComparison.OrdinalIgnoreCase)))
                        .Select(policy => new
                        {
                            policy.ProcessKey,
                            policy.ExecutablePath,
                            Architecture = (string?)null,
                            GraphicsApi = (GpuGraphicsApi?)null
                        }))
                    .Select(input => capabilityReader.Evaluate(
                        input.ProcessKey,
                        input.ExecutablePath,
                        input.Architecture,
                        input.GraphicsApi))
                    .ToArray();
                HardwareMetricSnapshot? hardware = null;
                try
                {
                    hardware = await metricSampler.GetSnapshotAsync(
                        MetricSampleRequest.CatalogProbe,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Settings remain readable while the inventory producer is temporarily unavailable.
                }

                var targetInventory = GpuPlacementTargetInventoryProjection.Create(
                    hardware,
                    new[] { softwarePolicy.StartupTargetGpu });
                return Results.Ok(new GpuPlacementSoftwareSettingsSnapshot(
                    softwarePolicy.SoftwareId,
                    softwarePolicy.SoftwareName,
                    softwarePolicy,
                    processPolicies,
                    processHistory,
                    startupInterceptions,
                    targetInventory,
                    capabilityInputs));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).AllowAnonymous();

        app.MapGet("/api/gpu-placement/software/{softwareId}/policy/default", (
            string softwareId,
            string? softwareName,
            string? softwareKind) =>
        {
            var resolvedName = string.IsNullOrWhiteSpace(softwareName) ? softwareId : softwareName.Trim();
            return Results.Ok(GpuPlacementPolicyDefaults.CreateSoftwarePolicy(
                softwareId,
                resolvedName,
                softwareKind));
        }).AllowAnonymous();

        app.MapPut("/api/gpu-placement/software/{softwareId}/policy", async (
            string softwareId,
            GpuPlacementSoftwarePolicy request,
            IGpuPlacementPolicyStore policyStore,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var saved = await policyStore.SaveSoftwarePolicyAsync(
                    request with { SoftwareId = softwareId },
                    cancellationToken);
                return await PublishSavedPolicyAsync(saved, runtimeSpecialization, "gpu-placement-software-policy-saved");
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPut("/api/gpu-placement/process-policy", async (
            GpuPlacementProcessPolicy request,
            IGpuPlacementPolicyStore policyStore,
            IGpuLaunchInterceptionRegistry launchInterceptionRegistry,
            GpuLaunchInterceptionReconciler launchInterceptionReconciler,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            CancellationToken cancellationToken) =>
        {
            GpuPlacementProcessPolicy saved;
            try
            {
                saved = await policyStore.SaveProcessPolicyAsync(request, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            RuntimePlanPublicationResult publication;
            try
            {
                publication = await runtimeSpecialization.RebuildAsync(
                    "gpu-placement-process-policy-saved",
                    CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(
                    new RuntimePolicySaveResult<GpuPlacementProcessPolicy>(
                        saved,
                        null,
                        "savedNotApplied",
                        "notAttempted",
                        null,
                        null,
                        0,
                        "runtime-plan-publication-failed"),
                    statusCode: StatusCodes.Status409Conflict);
            }

            GpuLaunchInterceptionStatus interception;
            try
            {
                var statuses = await launchInterceptionReconciler.ReconcileAsync(CancellationToken.None);
                interception = statuses.FirstOrDefault(status => string.Equals(
                    status.ExecutablePath, saved.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    ?? launchInterceptionRegistry.GetStatus(saved);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(
                    new RuntimePolicySaveResult<GpuPlacementProcessPolicy>(
                        saved,
                        null,
                        publication.HasDeliveryFailures
                            ? "committedWithDeliveryFailures"
                            : "committedAndApplied",
                        "applyFailed",
                        publication.Plan.Version,
                        publication.PublicationSequence,
                        publication.DeliveryFailures.Count,
                        "startup-interception-apply-failed"),
                    statusCode: StatusCodes.Status409Conflict);
            }

            var result = new RuntimePolicySaveResult<GpuPlacementProcessPolicy>(
                saved,
                interception,
                publication.HasDeliveryFailures
                    ? "committedWithDeliveryFailures"
                    : "committedAndApplied",
                "applied",
                publication.Plan.Version,
                publication.PublicationSequence,
                publication.DeliveryFailures.Count,
                null);
            return publication.HasDeliveryFailures
                ? Results.Json(result, statusCode: StatusCodes.Status202Accepted)
                : Results.Ok(result);
        });

        app.MapPost("/api/gpu-placement/process-history/observe", async (
            GpuPlacementProcessObservationRequest request,
            IGpuPlacementProcessHistoryStore processHistoryStore,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await processHistoryStore.ObserveAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }

    private static async Task<IResult> PublishSavedPolicyAsync<T>(
        T saved,
        IRuntimeSpecializationCoordinator runtimeSpecialization,
        string reason)
    {
        RuntimePlanPublicationResult publication;
        try
        {
            publication = await runtimeSpecialization.RebuildAsync(reason, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Results.Json(new RuntimePolicySaveResult<T>(
                saved, null, "savedNotApplied", "notAttempted", null, null, 0,
                "runtime-plan-publication-failed"), statusCode: StatusCodes.Status409Conflict);
        }

        var result = new RuntimePolicySaveResult<T>(
            saved, null,
            publication.HasDeliveryFailures ? "committedWithDeliveryFailures" : "committedAndApplied",
            "notAttempted", publication.Plan.Version, publication.PublicationSequence,
            publication.DeliveryFailures.Count, null);
        return publication.HasDeliveryFailures
            ? Results.Json(result, statusCode: StatusCodes.Status202Accepted)
            : Results.Ok(result);
    }
}
