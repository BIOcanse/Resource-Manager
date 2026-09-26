using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Application.Migration;
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
using ResourceManager.App.Domain.Migration;
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
                    new[] { softwarePolicy.StartupTargetGpu, softwarePolicy.TargetGpu }
                        .Concat(processPolicies.Select(static policy => policy.TargetGpu)));
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
                await runtimeSpecialization.RebuildAsync("gpu-placement-software-policy-saved", CancellationToken.None);
                return Results.Ok(saved);
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
                    new GpuPlacementProcessPolicySaveResult(
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
                interception = launchInterceptionRegistry.Apply(saved);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(
                    new GpuPlacementProcessPolicySaveResult(
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

            var result = new GpuPlacementProcessPolicySaveResult(
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

        app.MapPost("/api/gpu-placement/startup/resolve", async (
            HttpRequest request,
            IGpuStartupPlacementResolver resolver,
            CancellationToken cancellationToken) =>
        {
            var executablePath = await ReadLimitedUtf8BodyAsync(request, 32_767, cancellationToken);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return Results.BadRequest("invalid executable path");
            }

            var decision = await resolver.ResolveAsync(
                new GpuStartupPlacementRequest(executablePath),
                cancellationToken);
            return Results.Text(
                GpuLaunchBrokerProtocol.Serialize(decision),
                GpuLaunchBrokerProtocol.ContentType,
                System.Text.Encoding.UTF8);
        });

        app.MapPost("/api/gpu-placement/startup/report", async (
            HttpRequest request,
            IGpuLaunchExecutionReportStore reportStore,
            CancellationToken cancellationToken) =>
        {
            var protocol = await ReadLimitedUtf8BodyAsync(request, 64 * 1024, cancellationToken);
            if (!GpuLaunchBrokerProtocol.TryParseExecutionReport(protocol, out var report, out var error)
                || report is null)
            {
                return Results.BadRequest(error);
            }

            try
            {
                await reportStore.RecordAsync(report, cancellationToken);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
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

    private static async Task<string?> ReadLimitedUtf8BodyAsync(
        HttpRequest request,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0
            && request.ContentLength > ((long)maximumCharacters * 4L) + 4L)
        {
            return null;
        }

        using var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
        var buffer = new char[maximumCharacters + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }
            total += read;
        }

        return total > maximumCharacters
            ? null
            : new string(buffer, 0, total).Trim();
    }
}
