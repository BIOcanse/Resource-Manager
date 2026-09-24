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
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class RuntimePlanCompiler(
    IAppSettingsStore settingsStore,
    IDashboardSettingsStore dashboardSettingsStore,
    DashboardSettingsMigrator dashboardSettingsMigrator,
    DashboardMonitoringCatalogState dashboardMonitoringCatalog,
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
    RuntimePersistenceCapabilityPolicy persistenceCapabilities)
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
        var dashboardSettingsLoad = persistenceCapabilities.MutablePersistenceEnabled
                ? await dashboardSettingsStore.LoadAsync(cancellationToken)
                : await dashboardSettingsStore.LoadReadOnlyAsync(cancellationToken);
        var catalogSnapshot = dashboardMonitoringCatalog.Current;
        var dashboardSettings = dashboardSettingsMigrator.ResolveForRead(
            dashboardSettingsLoad,
            catalogSnapshot);
        if (persistenceCapabilities.MutablePersistenceEnabled
            && !dashboardSettingsMigrator.AreEquivalent(
                dashboardSettingsLoad.Settings,
                dashboardSettings))
        {
            _ = await dashboardSettingsStore.SaveAsync(
                dashboardSettings,
                cancellationToken);
        }
        if (catalogSnapshot is null)
        {
            dashboardSettings = WithoutUnverifiedGpuMonitoring(dashboardSettings);
        }
        var registeredSourceZoneIds = monitoringSourceZoneRegistry.GetSourceIds();
        var firmwareProviderPlan = firmwareProviderPlanCompiler.Compile();

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
            new CompiledHardwareScorePlan(
                AppSettingsNormalizer.NormalizeGpuPerformanceUseCases(settings.Performance.GpuPerformanceUseCases),
                gpuPerformanceScoreOverrideStore.LoadScores(),
                cpuTopology.CpuName,
                cpuOverrides.ScoresByCoreIndex),
            CompiledOptimizationModePlan.Compile(settings.Performance.OptimizationMode),
            new CompiledSelfLogicPlan(
                settings.Performance.MonitorRefreshIntervalMs,
                settings.Performance.ResourceTableRefreshIntervalMs,
                settings.Performance.ManagementRefreshIntervalMs,
                settings.Performance.DiscoveryRefreshIntervalMs,
                settings.Performance.OptimizationRefreshIntervalMs,
                settings.Performance.LocalSystemRefreshIntervalMs),
            CompileDiagnosticsPlan(settings.Debug),
            CompileMonitoringPlan(
                dashboardSettings,
                registeredSourceZoneIds,
                firmwareProviderPlan))
        {
            RuntimeCapabilityConstrainedPaths = runtimeProjection.ConstrainedPaths,
            AutoStartEnabled = settings.SystemIntegration.AutoStartEnabled,
            CpuBaseline = cpuBaseline,
            CpuPlacementTopology = cpuTopology
        };
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

    private static CompiledDiagnosticsPlan CompileDiagnosticsPlan(AppDebugSettings debug)
    {
        var debugModeEnabled = debug.DebugModeEnabled;
        var debugLogEnabled = debugModeEnabled && debug.DebugLogEnabled;
        return new CompiledDiagnosticsPlan(
            debugModeEnabled,
            debugLogEnabled,
            debugModeEnabled && debug.HostManagerSmartCoordinatorScoreOnlyEnabled,
            debugLogEnabled && debug.HostManagerSmartCoordinatorPerformanceLogEnabled);
    }

    private static CompiledMonitoringPlan CompileMonitoringPlan(
        DashboardSettings dashboardSettings,
        IReadOnlyList<string> registeredSourceZoneIds,
        CompiledFirmwareProviderPlan firmwareProviderPlan)
    {
        var dashboardMetricIds = dashboardSettings.Cards
            .SelectMany(static card => new[] { card.Main }.Concat(card.Small ?? []))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resourceBars = dashboardSettings.ResourceBars
            .Where(static bar => DashboardSettingsDefaults.IsResourceBarMetricSupported(bar.MetricId))
            .GroupBy(static bar => bar.MetricId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        var resourceBarMetricIds = resourceBars
            .Select(static bar => bar.MetricId.Trim())
            .ToArray();
        var resourceBarScaleModes = resourceBars.ToDictionary(
            static bar => bar.MetricId.Trim(),
            static bar => ResourceBreakdownScaleModes.Normalize(bar.MetricId, bar.ScaleMode),
            StringComparer.OrdinalIgnoreCase);

        return new CompiledMonitoringPlan(
            dashboardMetricIds.Length > 0 ? dashboardMetricIds : CompiledMonitoringPlan.Default.DashboardMetricIds,
            dashboardMetricIds.Length > 0 ? MetricSampleRequest.ForIds(dashboardMetricIds) : CompiledMonitoringPlan.Default.DashboardMetricRequest,
            resourceBarMetricIds.Length > 0 ? resourceBarMetricIds : CompiledMonitoringPlan.Default.ResourceBarMetricIds,
            resourceBarScaleModes.Count > 0 ? resourceBarScaleModes : CompiledMonitoringPlan.Default.ResourceBarScaleModes,
            CompileTableColumns(dashboardSettings.ResourceTableColumns, ResourceTableViewModes.Software),
            CompileTableColumns(dashboardSettings.ResourceTableProcessColumns, ResourceTableViewModes.Process),
            registeredSourceZoneIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            firmwareProviderPlan);
    }

    private static DashboardSettings WithoutUnverifiedGpuMonitoring(
        DashboardSettings settings)
    {
        return settings with
        {
            ResourceBars = settings.ResourceBars
                .Where(static bar => DashboardSettingsDefaults.ParseGpuIndex(bar.MetricId) is null)
                .ToArray(),
            ResourceTableColumns = settings.ResourceTableColumns
                .Where(static column => !ResourceTableColumnCatalog.IsGpuColumnId(column.Id))
                .ToArray(),
            ResourceTableProcessColumns = (settings.ResourceTableProcessColumns ?? [])
                .Where(static column => !ResourceTableColumnCatalog.IsGpuColumnId(column.Id))
                .ToArray()
        };
    }

    private static IReadOnlyList<string> CompileTableColumns(
        IReadOnlyList<ResourceTableColumnSettings>? columns,
        string viewMode)
    {
        var normalizedViewMode = viewMode.Equals(ResourceTableViewModes.Process, StringComparison.OrdinalIgnoreCase)
            ? ResourceTableViewModes.Process
            : ResourceTableViewModes.Software;
        var visible = columns?
            .Where(static column => column.Visible)
            .Select(static column => column.Id?.Trim())
            .Where(static id => !string.IsNullOrWhiteSpace(id) && ResourceTableColumnCatalog.IsKnownColumnId(id))
            .Where(id => normalizedViewMode.Equals(ResourceTableViewModes.Process, StringComparison.OrdinalIgnoreCase)
                || !DashboardSettingsDefaults.IsProcessDetailColumn(id!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static id => id!)
            .ToArray() ?? [];

        if (visible.Length > 0)
        {
            return visible;
        }

        return normalizedViewMode.Equals(ResourceTableViewModes.Process, StringComparison.OrdinalIgnoreCase)
            ? CompiledMonitoringPlan.Default.ProcessTableColumnIds
            : CompiledMonitoringPlan.Default.SoftwareTableColumnIds;
    }
}
