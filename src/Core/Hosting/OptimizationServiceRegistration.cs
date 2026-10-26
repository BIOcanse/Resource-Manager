using BlockUids = ResourceManager.App.StateItems.BlockUids;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Optimization.Scheduling;
using ResourceManager.App.Application.Optimization.SmartControl;
using ResourceManager.App.Application.Optimization.MemoryCleanup;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Diagnostics;
using ResourceManager.App.Infrastructure.Diagnostics;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.NativeScheduling;
using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Application.SoftwareIssues;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.Optimization.Transactions;
using ResourceManager.App.Infrastructure.Persistence.Legacy;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SoftwareIssues;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerOptimization(
        this IServiceCollection services,
        StartupCapabilitySet startupCapabilities)
    {
        services.AddSingleton<IGpuPerformanceScoreOverrideStore, JsonGpuPerformanceScoreOverrideStore>();
        services.AddSingleton<IDebugDiagnosticLogReader, JsonDebugDiagnosticLogReader>();
        if (startupCapabilities.Allows(StartupCapability.OptimizationRuntime)
            || startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            services.AddSingleton(static provider =>
                new NativeProcessPolicyBatchExecutor(
                    provider.GetRequiredService<Infrastructure.RuntimeSpecialization.HostManagerProcessPolicyExecutorRuntime>()));
            services.AddSingleton<IProcessResourcePolicyWriter>(static provider =>
                new WindowsProcessResourcePolicyWriter(
                    provider.GetRequiredService<NativeProcessPolicyBatchExecutor>()));
            services.AddSingleton<ResourceManagerSelfLocalResourceManager>();
        }
        if (!startupCapabilities.Allows(StartupCapability.OptimizationRuntime))
        {
            return services;
        }

        services.AddSingleton<IOptimizationProtectionStore, JsonOptimizationProtectionStore>();
        services.AddSingleton<IHostManagerRollbackStateStore, JsonHostManagerRollbackStateStore>();
        services.AddSingleton<HostManagerSchedulingAuthority>();
        services.AddSingleton<IHostManagerSchedulingAuthoritySource>(static provider =>
            provider.GetRequiredService<HostManagerSchedulingAuthority>());
        services.AddSingleton<IHostManagerMemoryModePolicySource,
            ProductBaselineHostManagerMemoryModePolicySource>();
        services.AddSingleton(static provider =>
            new HostManagerMemoryModePolicyAuthority(
                provider.GetRequiredService<IHostManagerMemoryModePolicySource>()));
        services.AddSingleton(static provider =>
            new HostManagerProcessPolicyTransaction(
                provider.GetRequiredService<IProcessResourcePolicyWriter>()));
        services.AddSingleton(static provider =>
            new HostManagerAdapterSchedulingTransaction(
                provider.GetRequiredService<IAdapterPolicyDispatcher>(),
                TimeProvider.System));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(static provider =>
        {
            var environment = provider.GetRequiredService<IHostEnvironment>();
            var retirements = new HostManagerAuthorityRetirementManager(environment);
            return new HostManagerNativeActionTransactionRuntime(
                provider.GetRequiredService<Infrastructure.RuntimeSpecialization.HostManagerTransactionJournalDeploymentRuntime>(),
                new HostManagerAppliedOwnershipRuntime(
                    provider.GetRequiredService<HostManagerAppliedOwnershipDeploymentRuntime>(),
                    retirements,
                    provider.GetRequiredService<ResourceManagerSelfLocalResourceManager>().CreateScratch),
                retirements,
                provider.GetRequiredService<ResourceManagerSelfLocalResourceManager>().CreateScratch);
        });
        services.AddSingleton<JsonDebugDiagnosticLogWriter>();
        services.AddSingleton<IDebugDiagnosticLogWriter>(static provider => provider.GetRequiredService<JsonDebugDiagnosticLogWriter>());
        services.AddHostedServiceAlias<JsonDebugDiagnosticLogWriter>(BlockUids.JsonDebugDiagnosticLogWriter, [BlockUids.RuntimeSpecializationCoordinator]);
        services.AddSingleton<NativeReportCoordinatorPersistenceStore>();
        services.AddSingleton<INativeReportCoordinatorPersistenceStore>(static provider =>
            provider.GetRequiredService<NativeReportCoordinatorPersistenceStore>());
        services.AddSingleton<OptimizationProtectionService>();
        services.AddSingleton<IOptimizationProtectionService>(static provider =>
            provider.GetRequiredService<OptimizationProtectionService>());
        services.AddSingleton(static provider => new HostManagerReportCoordinatorOwner(
            provider.GetRequiredService<RuntimePlanProvider>(),
            provider.GetRequiredService<HostManagerReportCoordinatorRuntime>(),
            provider.GetRequiredService<HostManagerRuntimeIdentity>(),
            provider.GetRequiredService<INativeReportCoordinatorPersistenceStore>(),
            provider.GetRequiredService<ResourceManager.App.Application.Metrics.IMetricSnapshotObservationSource>(),
            provider.GetRequiredService<ResourceManager.App.Application.ResourceBreakdown.IResourceBreakdownObservationSource>(),
            provider.GetRequiredService<ResourceManager.App.Application.SystemHealth.ISystemInterruptSnapshotSource>(),
            provider.GetRequiredService<IOptimizationProtectionService>(),
            provider.GetRequiredService<ILogger<HostManagerReportCoordinatorOwner>>(),
            provider.GetRequiredService<ResourceManagerSelfLocalResourceManager>().CreateScratch,
            provider.GetRequiredService<ResourceManager.App.Application.ResourceBreakdown.ISchedulingProcessFactObservationSource>(),
            provider.GetRequiredService<ResourceManager.App.Application.Software.ISoftwareRegistryView>()));
        services.AddSingleton<IHostManagerReportService>(static provider =>
            provider.GetRequiredService<HostManagerReportCoordinatorOwner>());
        services.AddSingleton<IOptimizationSoftwareIssueSignalSource>(static provider =>
            provider.GetRequiredService<HostManagerReportCoordinatorOwner>());
        services.AddSingleton<ISoftwareIssueSource, OptimizationReportSoftwareIssueSource>();
        services.AddHostedServiceAlias<HostManagerReportCoordinatorOwner>(BlockUids.HostManagerReportCoordinatorOwner,
            [BlockUids.RuntimeSpecializationCoordinator, BlockUids.WindowsResourceBreakdownSampler, BlockUids.WindowsHardwareMetricSampler,
                BlockUids.EtwSystemInterruptSnapshotSource,
                ..(startupCapabilities.Allows(StartupCapability.MutablePersistence) ? new[] { BlockUids.SqlitePortableSoftwareRegistry } : [])]);

        services.AddSingleton<IAutomaticMemoryCleanupPlanner, NativeAutomaticMemoryCleanupPlanner>();
        services.AddSingleton<HostManagerMemoryCleanupAttemptJournal>();
        services.AddSingleton<IWindowsJobMembershipProbe, WindowsJobMembershipProbe>();
        services.AddSingleton(static provider =>
            new HostManagerProcessEffectValidationScopeAuthority(
                provider.GetRequiredService<IHostEnvironment>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IWindowsJobMembershipProbe>()));
        services.AddHostManagerSmartCoordinatorOwner();
        services.AddHostManagerSmartControlZone<HostManagerCoordinatorControlZone>();
        services.AddHostManagerSmartControlZone<HostManagerSamplingControlZone>();
        services.AddHostManagerSmartControlZone<HostManagerScoringControlZone>();
        services.AddHostManagerSmartControlZone<HostManagerPolicyExecutionControlZone>();
        services.AddHostManagerSmartControlZone<HostManagerHardwarePlacementControlZone>();
        services.AddSingleton<HostManagerSmartControlZoneRegistry>();
        services.AddSingleton<IHostManagerSmartControlZoneRegistry>(static provider =>
            provider.GetRequiredService<HostManagerSmartControlZoneRegistry>());
        return services;
    }

    internal static IServiceCollection AddHostManagerSmartCoordinatorOwner(
        this IServiceCollection services,
        bool startHostedService = true)
    {
        services.AddSingleton<RuntimeCpuLeaseStore>();
        services.AddSingleton<HostManagerSmartCoordinator>();
        services.AddSingleton<IRuntimeCpuLeaseService>(static provider =>
            provider.GetRequiredService<HostManagerSmartCoordinator>());
        services.AddSingleton<IHostManagerSmartCoordinator>(static provider =>
            provider.GetRequiredService<HostManagerSmartCoordinator>());
        services.AddSingleton<IHostManagerProcessEffectValidationScopeControl>(static provider =>
            provider.GetRequiredService<HostManagerSmartCoordinator>());
        services.AddSingleton<IHostManagerMemoryCleanupValidationEvidenceControl>(static provider =>
            provider.GetRequiredService<HostManagerSmartCoordinator>());
        if (startHostedService)
        {
            services.AddHostedServiceAlias<HostManagerSmartCoordinator>(BlockUids.HostManagerSmartCoordinator, [BlockUids.RuntimeSpecializationCoordinator, BlockUids.WindowsResourceBreakdownSampler, BlockUids.WindowsHardwareMetricSampler, BlockUids.EtwCpuCoreResidencyReader]);
        }
        return services;
    }

    private static IServiceCollection AddHostManagerSmartControlZone<TZone>(this IServiceCollection services)
        where TZone : HostManagerSmartControlZone
    {
        services.AddSingleton<TZone>();
        services.AddSingleton<HostManagerSmartControlZone>(static provider => provider.GetRequiredService<TZone>());
        services.AddSingleton<IResourceManagerSelfComputeZone>(static provider => provider.GetRequiredService<TZone>());
        return services;
    }

}
