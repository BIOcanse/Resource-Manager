using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.Optimization.Scheduling;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Application.Optimization.Scoring;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class RuntimePlanCompiler(
    IAppSettingsStore settingsStore,
    IGpuPlacementPolicyStore gpuPlacementPolicyStore,
    IGpuPerformanceScoreOverrideStore gpuPerformanceScoreOverrideStore,
    ICpuCorePerformanceOverrideStore cpuCorePerformanceOverrideStore,
    CpuBaselineRatioCatalog cpuBaselineRatioCatalog,
    ICpuTopologySampler cpuTopologySampler,
    IMonitoringSourceZoneRegistry monitoringSourceZoneRegistry,
    FirmwareProviderPlanCompiler firmwareProviderPlanCompiler,
    StrictHostManagerProfileLoader hostManagerProfileLoader,
    HostManagerPlanCompiler hostManagerPlanCompiler,
    RuntimeExecutionCapabilityPolicy executionCapabilities,
    RuntimePersistenceCapabilityPolicy persistenceCapabilities,
    ISoftwarePolicyProfileProvider softwarePolicyProfileProvider,
    ILogger<RuntimePlanCompiler>? logger = null)
{
    public async Task<CompiledRuntimePlan> CompileAsync(
        long version,
        string reason,
        CancellationToken cancellationToken)
    {
        var settingsLoad = persistenceCapabilities.MutablePersistenceEnabled
            ? await settingsStore.LoadAsync(cancellationToken)
            : await settingsStore.LoadReadOnlyAsync(cancellationToken);
        return await CompileAsync(
            version,
            reason,
            settingsLoad,
            cancellationToken);
    }

    internal async Task<CompiledRuntimePlan> CompileAsync(
        long version,
        string reason,
        AppSettingsUpdateResult settingsLoad,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settingsLoad);
        var runtimeProjection = AppSettingsRuntimeCapabilityProjector.Evaluate(
            AppSettingsNormalizer.Normalize(settingsLoad.Settings),
            executionCapabilities);
        var settings = runtimeProjection.Settings;
        var normalizedSettingsInput = settingsLoad with { Settings = settings };
        var cpuTopology = cpuTopologySampler.CaptureTopology();
        var cpuOverrides = cpuCorePerformanceOverrideStore.LoadConfiguration(cpuTopology.CpuName);
        var cpuBaseline = cpuBaselineRatioCatalog.Resolve(cpuTopology, cpuOverrides.BaselineRatio);
        var cpuScoring = CpuScoringPlanCompiler.Compile(
            cpuTopology, cpuOverrides.ScoresByCoreIndex, cpuBaseline.Ratio);
        var hostManagerPlan = hostManagerPlanCompiler.Compile(
            hostManagerProfileLoader.Load(),
            normalizedSettingsInput,
            checked((ulong)version),
            cpuTopology,
            cpuScoring);
        var gpuPolicyDocument = await gpuPlacementPolicyStore.GetAsync(cancellationToken);
        // Event-driven: loaded with the immutable runtime plan, never on a scheduling read.
        var catalog = await softwarePolicyProfileProvider.GetProfileAsync(cancellationToken);
        var registeredSourceZoneIds = monitoringSourceZoneRegistry.GetSourceIds();
        var firmwareProviderPlan = firmwareProviderPlanCompiler.Compile();
        var hardwareScores = CompileHardwareScores(
            cpuTopology.CpuName, cpuOverrides.ScoresByCoreIndex, gpuPerformanceScoreOverrideStore);
        if (hardwareScores.GpuPerformanceScoreUnavailableReason is { } scoreError)
            logger?.LogWarning("{GpuPerformanceScoreUnavailableReason}", scoreError);

        return new CompiledRuntimePlan(
            version,
            DateTimeOffset.Now,
            string.IsNullOrWhiteSpace(reason) ? "manual" : reason.Trim(),
            hostManagerPlan,
            CompileGpuPlacementPlan(
                settings.Performance.PreciseGpuPlacementEnabled,
                gpuPolicyDocument,
                cpuTopology),
            OptimizationBaseScorePolicyResolver.Compile(gpuPolicyDocument, catalog),
            hardwareScores,
            CompiledOptimizationModePlan.Compile(settings.Performance.OptimizationMode),
            CompileDiagnosticsPlan(settings.Debug),
            CompileMonitoringPlan(registeredSourceZoneIds, firmwareProviderPlan))
        {
            RuntimeCapabilityConstrainedPaths = runtimeProjection.ConstrainedPaths,
            AutoStartEnabled = settings.SystemIntegration.AutoStartEnabled,
            AutomaticUpdatesEnabled = settings.Update is { Mode: not AppUpdateModes.Off, PeriodicCheckEnabled: true },
            MemoryProtection = settings.MemoryProtection ?? new(),
            ServiceRecovery = settings.SystemIntegration,
            CpuBaseline = cpuBaseline,
            CpuPlacementTopology = cpuTopology
        };
    }

    internal static CompiledHardwareScorePlan CompileHardwareScores(string cpuName, IReadOnlyDictionary<int, double> cpuScores, IGpuPerformanceScoreOverrideStore gpuStore)
    {
        try
        {
            return new(gpuStore.LoadScores(), cpuName, cpuScores);
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or IOException
            or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(new Dictionary<string, double>(), cpuName, cpuScores)
            {
                GpuPerformanceScoreUnavailableReason = $"GPU performance overrides unavailable: {error.Message}"
            };
        }
    }

    internal static CompiledGpuPlacementPlan CompileGpuPlacementPlan(
        bool globalPreciseProviderEnabled,
        GpuPlacementPolicyDocument gpuPolicyDocument,
        CpuTopologySnapshot cpuTopology)
    {
        var softwarePolicies = (gpuPolicyDocument.SoftwarePolicies ?? [])
            .Where(static policy => !string.IsNullOrWhiteSpace(policy.SoftwareId))
            .GroupBy(static policy => policy.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(policy => policy.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var resolvedSoftwarePolicies = softwarePolicies.ToDictionary(
            static pair => pair.Key,
            pair => CompileResolvedGpuPlacementPolicy(ResolvedGpuPlacementPolicy.FromSoftware(pair.Value), cpuTopology),
            StringComparer.OrdinalIgnoreCase);
        var processPolicies = new Dictionary<string, ResolvedGpuPlacementPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var processPolicy in gpuPolicyDocument.ProcessPolicies ?? [])
        {
            if (string.IsNullOrWhiteSpace(processPolicy.SoftwareId)
                || string.IsNullOrWhiteSpace(processPolicy.ProcessKey)
                || processPolicy.Inherit
                || !softwarePolicies.TryGetValue(processPolicy.SoftwareId, out var softwarePolicy)
                || !softwarePolicy.ProcessOverrideAllowed)
            {
                continue;
            }

            processPolicies[CompiledBaseScorePlan.CreateProcessPolicyKey(
                processPolicy.SoftwareId,
                processPolicy.ProcessKey)] = CompileResolvedGpuPlacementPolicy(
                ResolvedGpuPlacementPolicy.FromProcess(processPolicy, softwarePolicy),
                cpuTopology);
        }

        return new CompiledGpuPlacementPlan(
            globalPreciseProviderEnabled,
            resolvedSoftwarePolicies,
            processPolicies)
        {
            KindDefaultRuntimeHotSwitchSoftwareIds = softwarePolicies.Values
                .Where(static policy => policy.RuntimeHotSwitchEnabled is null)
                .Select(static policy => policy.SoftwareId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ResolvedGpuPlacementPolicy CompileResolvedGpuPlacementPolicy(
        ResolvedGpuPlacementPolicy policy,
        CpuTopologySnapshot cpuTopology)
    {
        return policy with
        {
            CpuManualExclusivePositionIds = CpuPositionIdResolver.ExpandToPhysicalCoreIds(
                policy.CpuManualExclusivePositionIds,
                cpuTopology),
            CpuManualLockedPositionIds = CpuPositionIdResolver.ExpandToPhysicalCoreIds(
                policy.CpuManualLockedPositionIds,
                cpuTopology)
        };
    }

    internal static CompiledDiagnosticsPlan CompileDiagnosticsPlan(AppDebugSettings debug)
    {
        var debugModeEnabled = debug.DebugModeEnabled;
        var debugLogEnabled = debugModeEnabled && debug.DebugLogEnabled;
        return new CompiledDiagnosticsPlan(
            debugModeEnabled,
            debugLogEnabled,
            debug.HostManagerSmartCoordinatorScoreOnlyEnabled,
            debugLogEnabled && debug.HostManagerSmartCoordinatorPerformanceLogEnabled);
    }

    private static CompiledMonitoringPlan CompileMonitoringPlan(
        IReadOnlyList<string> registeredSourceZoneIds,
        CompiledFirmwareProviderPlan firmwareProviderPlan) => new(
            registeredSourceZoneIds.Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            firmwareProviderPlan);
}
