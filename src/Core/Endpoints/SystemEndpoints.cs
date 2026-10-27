using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Optimization.Scheduling;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapSystemEndpoints(
        this IEndpointRouteBuilder app,
        StartupCapabilitySet startupCapabilities)
    {
        app.MapCpuBaselineRatioEndpoints(startupCapabilities);
        app.MapGet("/api/local-system/status", (ILocalSystemStatusProvider statusProvider) =>
        {
            return Results.Ok(statusProvider.GetStatus());
        }).AllowAnonymous();

        app.MapGet("/api/system/host-manager/deployment", (
            HttpResponse response,
            IHostManagerDeploymentState deploymentState) =>
        {
            DisableResponseCache(response);
            return Results.Ok(deploymentState.CaptureDiagnostics());
        }).AllowAnonymous();

        app.MapGet("/api/cpu/topology", async (
            HttpResponse response,
            ICpuTopologyReader topologyReader,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            await response.WriteAsJsonAsync<object>(await topologyReader.FetchOnceAsync("cpu-topology:snapshot", cancellationToken) is { } topology
                ? topology : new CpuObservationUnavailable("unavailable", "cpu-topology-not-published"), cancellationToken);
        }).AllowAnonymous();

        app.MapGet("/api/cpu/topology/fields", async (
            HttpResponse response,
            ICpuTopologyReader topologyReader, CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok(await topologyReader.FetchInformationOnceAsync("cpu-topology:fields", cancellationToken)
                ?? DeviceInformationSnapshot.Empty);
        }).AllowAnonymous();

        app.MapGet("/api/cpu/topology/exclusive-bindings", async (
            HttpResponse response,
            ICpuTopologyReader topologyReader,
            IGpuPlacementPolicyStore policyStore,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var topology = await topologyReader.FetchOnceAsync("cpu-topology:exclusive-bindings", cancellationToken);
            var snapshot = topology is null
                ? null
                : CpuExclusiveBindingProjection.Create(
                    topology,
                    await policyStore.GetAsync(cancellationToken),
                    DateTimeOffset.UtcNow);
            await response.WriteAsJsonAsync<object>(snapshot is not null ? snapshot
                : new CpuObservationUnavailable("unavailable", "cpu-topology-not-published"), cancellationToken);
        }).AllowAnonymous();

        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            app.MapPut("/api/cpu/topology/performance-overrides", async (
                CpuCorePerformanceOverrideRequest request,
                ICpuCorePerformanceOverrideStore overrideStore,
                IRuntimeSpecializationCoordinator runtimeSpecialization,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = overrideStore.Save(request);
                    return await PublishSavedPolicyAsync(result, runtimeSpecialization, "cpu-performance-overrides-saved");
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new EndpointErrorPayload(ex.Message));
                }
            });

            app.MapDelete("/api/cpu/topology/performance-overrides", async (
                string cpuName,
                ICpuCorePerformanceOverrideStore overrideStore,
                IRuntimeSpecializationCoordinator runtimeSpecialization,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = overrideStore.Reset(cpuName);
                    return await PublishSavedPolicyAsync(result, runtimeSpecialization, "cpu-performance-overrides-reset");
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new EndpointErrorPayload(ex.Message));
                }
            });
        }

        app.MapGet("/api/gpu/performance-overrides", (
            HttpResponse response,
            IGpuPerformanceScoreOverrideStore overrideStore) =>
        {
            DisableResponseCache(response);
            return Results.Ok(overrideStore.Load());
        }).AllowAnonymous();

        app.MapGet("/api/gpu/performance-scores", async (
            HttpResponse response,
            IMetricSampler metricSampler,
            IGpuPerformanceScoreOverrideStore overrideStore,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var hardware = await metricSampler.GetSnapshotAsync(MetricSampleRequest.CatalogProbe, cancellationToken);
            var loaded = overrideStore.Load();
            var items = hardware.Gpus
                .OrderBy(static gpu => gpu.Index)
                .Select(gpu =>
                {
                    var gpuId = GpuPerformanceScoreIds.FromIndex(gpu.Index);
                    var preset = GpuPerformanceScorePresetResolver.Resolve(
                        gpu.Name,
                        gpu.TotalMemoryBytes,
                        gpu.StandardGraphicsFrequencyMhz);
                    var hasOverride = loaded.ScoresByGpuId.TryGetValue(gpuId, out var overrideScore);
                    return new GpuPerformanceScoreItem(
                        gpuId,
                        gpu.Index,
                        gpu.Name,
                        preset.RasterScore,
                        preset.Score,
                        hasOverride ? GpuPerformanceScorePresetResolver.NormalizeManualScore(overrideScore) : preset.Score,
                        hasOverride,
                        preset.IsIntegrated,
                        preset.Source,
                        preset.MatchedPreset,
                        preset.IsPresetMatch) { HasDedicatedMemoryMetrics = gpu.HasDedicatedMemoryMetrics };
                })
                .ToArray();
            return Results.Ok(new GpuPerformanceScoreSnapshot(
                hardware.CapturedAt,
                items,
                loaded.StoragePath));
        }).AllowAnonymous();

        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            app.MapPut("/api/gpu/performance-overrides", async (
                GpuPerformanceScoreOverrideRequest request,
                IGpuPerformanceScoreOverrideStore overrideStore,
                IRuntimeSpecializationCoordinator runtimeSpecialization,
                CancellationToken cancellationToken) =>
            {
                var result = overrideStore.Save(request);
                return await PublishSavedPolicyAsync(result, runtimeSpecialization, "gpu-performance-overrides-saved");
            });

            app.MapDelete("/api/gpu/performance-overrides", async (
                IGpuPerformanceScoreOverrideStore overrideStore,
                IRuntimeSpecializationCoordinator runtimeSpecialization,
                CancellationToken cancellationToken) =>
            {
                var result = overrideStore.Reset();
                return await PublishSavedPolicyAsync(result, runtimeSpecialization, "gpu-performance-overrides-reset");
            });
        }

        app.MapGet("/api/cpu/residency", async (
            HttpResponse response,
            ICpuCoreResidencyReader residencyReader,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok<object>(await residencyReader.FetchOnceAsync("cpu-residency:snapshot", cancellationToken) is { } snapshot ? snapshot
                : new CpuObservationUnavailable("unavailable", "cpu-residency-window-unavailable"));
        }).AllowAnonymous();

        app.MapGet("/api/cpu/residency/cores", async (HttpResponse response, ICpuCoreResidencyReader residencyReader,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            return Results.Ok<object>(await residencyReader.FetchOnceAsync("cpu-residency:cores", cancellationToken) is { } snapshot
                ? CpuObservationProjection.Summarize(snapshot)
                : new CpuObservationUnavailable("unavailable", "cpu-residency-window-unavailable"));
        }).AllowAnonymous();

        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            app.MapPost("/api/system/open-path", (
                LocalPathOpenRequest request,
                HttpContext context,
                ILocalPathOpener pathOpener) =>
            {
                if (!TryReadInteractiveCaller(context, out var interactiveCaller))
                    return Results.Json(new EndpointCodePayload("InteractiveUserSessionRequired"), statusCode: StatusCodes.Status403Forbidden);
                try
                {
                    return Results.Ok(pathOpener.Open(request, interactiveCaller));
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.Json(new EndpointCodePayload("InteractiveUserSessionRequired"), statusCode: StatusCodes.Status403Forbidden);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new EndpointErrorPayload(ex.Message));
                }
            });

            app.MapPost("/api/system/open-properties", (
                LocalPathPropertiesRequest request,
                HttpContext context,
                ISystemProcessActionService processActions) =>
            {
                if (!TryReadInteractiveCaller(context, out var caller))
                    return Results.Json(new EndpointCodePayload("InteractiveUserSessionRequired"), statusCode: StatusCodes.Status403Forbidden);
                try
                {
                    return Results.Ok(processActions.OpenProperties(request, caller));
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.Json(new EndpointCodePayload("InteractiveUserSessionRequired"), statusCode: StatusCodes.Status403Forbidden);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new EndpointErrorPayload(ex.Message));
                }
            });

            app.MapPost("/api/system/search-online", (
                LocalOnlineSearchRequest request,
                HttpContext context,
                ISystemProcessActionService processActions) =>
            {
                if (!TryReadInteractiveCaller(context, out var caller))
                    return Results.Json(new EndpointCodePayload("InteractiveUserSessionRequired"), statusCode: StatusCodes.Status403Forbidden);
                try
                {
                    return Results.Ok(processActions.SearchOnline(request, caller));
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.Json(new EndpointCodePayload("InteractiveUserSessionRequired"), statusCode: StatusCodes.Status403Forbidden);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new EndpointErrorPayload(ex.Message));
                }
            });

            app.MapPost("/api/system/processes/terminate", (
                SystemProcessOperationRequest request,
                HttpContext context,
                ISystemProcessActionService processActions) =>
            {
                if (!WindowsLoopbackApiAdministratorReader.TryOpenCallerToken(context, out var caller, out var token))
                    return Results.Json(new EndpointCodePayload("ProcessActionRequiresElevation"), statusCode: StatusCodes.Status403Forbidden);
                using var callerToken = token;
                if (caller.ProcessId == Environment.ProcessId || !caller.IsAdministrator)
                    return Results.Json(new EndpointCodePayload("ProcessActionRequiresElevation"), statusCode: StatusCodes.Status403Forbidden);
                try
                {
                    return Results.Ok(processActions.TerminateProcesses(request, callerToken));
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.Json(new EndpointCodePayload("ProcessActionRequiresElevation"), statusCode: StatusCodes.Status403Forbidden);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new EndpointErrorPayload(ex.Message));
                }
            });

            app.MapPost("/api/system/processes/dump", (
                SystemProcessOperationRequest request,
                HttpContext context,
                ISystemProcessActionService processActions) =>
            {
                if (!WindowsLoopbackApiAdministratorReader.TryOpenCallerToken(context, out var caller, out var token))
                    return Results.Json(new EndpointCodePayload("ProcessActionRequiresElevation"), statusCode: StatusCodes.Status403Forbidden);
                using var callerToken = token;
                if (caller.ProcessId == Environment.ProcessId || !caller.IsAdministrator)
                    return Results.Json(new EndpointCodePayload("ProcessActionRequiresElevation"), statusCode: StatusCodes.Status403Forbidden);
                try
                {
                    return Results.Ok(processActions.CreateProcessDumps(request, callerToken));
                }
                catch (UnauthorizedAccessException)
                {
                    return Results.Json(new EndpointCodePayload("ProcessActionRequiresElevation"), statusCode: StatusCodes.Status403Forbidden);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new EndpointErrorPayload(ex.Message));
                }
            });
        }

        return app;
    }

    private static bool TryReadInteractiveCaller(HttpContext context, out SoftwareOperationCaller caller)
    {
        caller = null!;
        if (!WindowsLoopbackApiAdministratorReader.TryReadCallerIdentity(context, out var identity)
            || identity.ProcessId == Environment.ProcessId
            || identity.WindowsSessionId <= 0)
            return false;

        caller = new SoftwareOperationCaller(identity.WindowsSid, checked((uint)identity.WindowsSessionId));
        return true;
    }
}
