using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.Optimization.Scheduling;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
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
    IAdapterSoftwareRegistry adapterSoftwareRegistry,
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
        var adapterRegistrations = await adapterSoftwareRegistry.GetAllAsync(cancellationToken);
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
            CompileAdapterDispatchPlan(
                gpuPolicyDocument,
                adapterRegistrations),
            CompileGpuPlacementPlan(
                settings.Performance.PreciseGpuPlacementEnabled,
                gpuPolicyDocument,
                cpuTopology),
            CompileBaseScorePlan(gpuPolicyDocument),
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

    private static CompiledAdapterDispatchPlan CompileAdapterDispatchPlan(
        GpuPlacementPolicyDocument gpuPolicyDocument,
        IReadOnlyList<AdapterSoftwareRegistration> adapterRegistrations)
    {
        var softwarePolicies = (gpuPolicyDocument.SoftwarePolicies ?? [])
            .Where(static policy => !string.IsNullOrWhiteSpace(policy.SoftwareId))
            .GroupBy(static policy => policy.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(policy => policy.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var capabilityIds = adapterRegistrations
            .Where(static registration => registration.SchedulingCapabilities?.HasAnyDimension == true)
            .Select(static registration => registration.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        capabilityIds.Add(RuntimeAttributionIds.ResourceManagerSelf);
        var registrationsById = adapterRegistrations
            .Where(static registration => !string.IsNullOrWhiteSpace(registration.Id))
            .GroupBy(static registration => registration.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(registration => registration.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);

        var routeIds = adapterRegistrations
            .Select(static registration => registration.Id)
            .Concat(softwarePolicies.Keys)
            .Append(RuntimeAttributionIds.ResourceManagerSelf)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var routes = new Dictionary<string, CompiledAdapterDispatchRoute>(StringComparer.OrdinalIgnoreCase);
        var supportedCpuGrades = new Dictionary<string, IReadOnlyList<AdapterCpuSchedulingGrade>>(StringComparer.OrdinalIgnoreCase);
        var supportedGpuGrades = new Dictionary<string, IReadOnlyList<AdapterGpuSchedulingGrade>>(StringComparer.OrdinalIgnoreCase);
        foreach (var softwareId in routeIds)
        {
            routes[softwareId] = capabilityIds.Contains(softwareId)
                ? CompiledAdapterDispatchRoute.SoftwareLevelScheduler
                : CompiledAdapterDispatchRoute.ConstraintActions;
            var capabilities = registrationsById.TryGetValue(softwareId, out var registration)
                ? registration.SchedulingCapabilities
                : null;
            if (softwareId.Equals(RuntimeAttributionIds.ResourceManagerSelf, StringComparison.OrdinalIgnoreCase))
            {
                supportedCpuGrades[softwareId] = ResourceManagerSelfDescriptor.SupportedCpuSchedulingGrades;
                supportedGpuGrades[softwareId] = ResourceManagerSelfDescriptor.SupportedGpuSchedulingGrades;
                continue;
            }

            supportedCpuGrades[softwareId] = capabilities?.Cpu?.SupportedGrades ?? [];
            supportedGpuGrades[softwareId] = capabilities?.Gpu?.SupportedGrades ?? [];
        }

        return new CompiledAdapterDispatchPlan(
            routes,
            supportedCpuGrades,
            supportedGpuGrades);
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

    private static CompiledBaseScorePlan CompileBaseScorePlan(GpuPlacementPolicyDocument gpuPolicyDocument)
    {
        var softwarePolicies = (gpuPolicyDocument.SoftwarePolicies ?? [])
            .Where(static policy => !string.IsNullOrWhiteSpace(policy.SoftwareId))
            .GroupBy(static policy => policy.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(policy => policy.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var softwareScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in softwarePolicies.Values)
        {
            var score = SanitizeBaseScore(policy.BaseScoreOverride);
            if (score.HasValue)
            {
                softwareScores[policy.SoftwareId] = score.Value;
            }
        }

        var processScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
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

            var score = SanitizeBaseScore(processPolicy.BaseScoreOverride);
            if (score.HasValue)
            {
                processScores[CompiledBaseScorePlan.CreateProcessPolicyKey(
                    processPolicy.SoftwareId,
                    processPolicy.ProcessKey)] = score.Value;
            }
        }

        return new CompiledBaseScorePlan(softwareScores, processScores);
    }

    private static double? SanitizeBaseScore(double? value)
    {
        return value.HasValue && double.IsFinite(value.Value)
            ? Math.Round(Math.Clamp(value.Value, 0, 100), 2)
            : null;
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
