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
using ResourceManager.App.Domain.Migration;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ResourceTable;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Endpoints.Transport;

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
            await response.WriteAsJsonAsync(topologyReader.GetSnapshot(), cancellationToken);
        }).AllowAnonymous();

        app.MapGet("/api/cpu/topology/exclusive-bindings", async (
            HttpResponse response,
            ICpuTopologyReader topologyReader,
            IGpuPlacementPolicyStore policyStore,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var topology = topologyReader.GetSnapshot();
            var snapshot = topology is null
                ? null
                : CpuExclusiveBindingProjection.Create(
                    topology,
                    await policyStore.GetAsync(cancellationToken),
                    DateTimeOffset.UtcNow);
            await response.WriteAsJsonAsync(snapshot, cancellationToken);
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
                    await runtimeSpecialization.RebuildAsync("cpu-performance-overrides-saved", CancellationToken.None);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
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
                    await runtimeSpecialization.RebuildAsync("cpu-performance-overrides-reset", CancellationToken.None);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
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
            IAppSettingsStore settingsStore,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var hardware = await metricSampler.GetSnapshotAsync(MetricSampleRequest.CatalogProbe, cancellationToken);
            var loaded = overrideStore.Load();
            var appSettings = await settingsStore.LoadReadOnlyAsync(cancellationToken);
            var gpuPerformanceUseCases = appSettings.Settings.Performance.GpuPerformanceUseCases;
            var items = hardware.Gpus
                .OrderBy(static gpu => gpu.Index)
                .Select(gpu =>
                {
                    var gpuId = GpuPerformanceScoreIds.FromIndex(gpu.Index);
                    var preset = GpuPerformanceScorePresetResolver.Resolve(
                        gpu.Name,
                        gpu.TotalMemoryBytes,
                        gpu.StandardGraphicsFrequencyMhz,
                        gpuPerformanceUseCases);
                    var hasOverride = loaded.ScoresByGpuId.TryGetValue(gpuId, out var overrideScore);
                    return new GpuPerformanceScoreItem(
                        gpuId,
                        gpu.Index,
                        gpu.Name,
                        preset.RasterScore,
                        preset.GenerationBonusScore,
                        preset.UseCaseBonusScore,
                        preset.GpuPerformanceUseCases,
                        preset.Score,
                        hasOverride ? GpuPerformanceScorePresetResolver.NormalizeManualScore(overrideScore) : preset.Score,
                        hasOverride,
                        preset.IsIntegrated,
                        preset.Source,
                        preset.MatchedPreset);
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
                await runtimeSpecialization.RebuildAsync("gpu-performance-overrides-saved", CancellationToken.None);
                return Results.Ok(result);
            });

            app.MapDelete("/api/gpu/performance-overrides", async (
                IGpuPerformanceScoreOverrideStore overrideStore,
                IRuntimeSpecializationCoordinator runtimeSpecialization,
                CancellationToken cancellationToken) =>
            {
                var result = overrideStore.Reset();
                await runtimeSpecialization.RebuildAsync("gpu-performance-overrides-reset", CancellationToken.None);
                return Results.Ok(result);
            });
        }

        app.MapGet("/api/cpu/residency", (
            HttpResponse response,
            ICpuCoreResidencyReader residencyReader) =>
        {
            DisableResponseCache(response);
            return Results.Ok(residencyReader.Read());
        }).AllowAnonymous();

        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            app.MapPost("/api/system/open-path", (
                LocalPathOpenRequest request,
                ILocalPathOpener pathOpener) =>
            {
                try
                {
                    return Results.Ok(pathOpener.Open(request));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/system/open-properties", (
                LocalPathPropertiesRequest request,
                ISystemProcessActionService processActions) =>
            {
                try
                {
                    return Results.Ok(processActions.OpenProperties(request));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/system/search-online", (
                LocalOnlineSearchRequest request,
                ISystemProcessActionService processActions) =>
            {
                try
                {
                    return Results.Ok(processActions.SearchOnline(request));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/system/processes/terminate", (
                SystemProcessOperationRequest request,
                ISystemProcessActionService processActions) =>
            {
                try
                {
                    return Results.Ok(processActions.TerminateProcesses(request));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/system/processes/dump", (
                SystemProcessOperationRequest request,
                ISystemProcessActionService processActions) =>
            {
                try
                {
                    return Results.Ok(processActions.CreateProcessDumps(request));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });
        }

        return app;
    }
}
