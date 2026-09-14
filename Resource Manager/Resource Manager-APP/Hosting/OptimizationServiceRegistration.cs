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
        if (!startupCapabilities.Allows(StartupCapability.OptimizationRuntime))
        {
            return services;
        }

        services.AddSingleton<IOptimizationProtectionStore, JsonOptimizationProtectionStore>();
        services.AddSingleton<IHostManagerRollbackStateStore, JsonHostManagerRollbackStateStore>();
        services.AddSingleton(static provider =>
            new NativeProcessPolicyBatchExecutor(
                provider.GetRequiredService<Infrastructure.RuntimeSpecialization.HostManagerProcessPolicyExecutorRuntime>()));
        services.AddSingleton<IProcessResourcePolicyWriter>(static provider =>
            new WindowsProcessResourcePolicyWriter(
                provider.GetRequiredService<NativeProcessPolicyBatchExecutor>()));
        services.AddSingleton<ResourceManagerSelfLocalResourceManager>();
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
                    retirements),
                retirements);
        });
        services.AddSingleton<JsonDebugDiagnosticLogWriter>();
        services.AddSingleton<IDebugDiagnosticLogWriter>(static provider => provider.GetRequiredService<JsonDebugDiagnosticLogWriter>());
        services.AddHostedServiceAlias<JsonDebugDiagnosticLogWriter>();
        services.AddSingleton<NativeReportCoordinatorPersistenceStore>();
        services.AddSingleton<INativeReportCoordinatorPersistenceStore>(static provider =>
            provider.GetRequiredService<NativeReportCoordinatorPersistenceStore>());
        services.AddSingleton<OptimizationProtectionService>();
        services.AddSingleton<IOptimizationProtectionService>(static provider =>
            provider.GetRequiredService<OptimizationProtectionService>());
        services.AddSingleton<HostManagerReportCoordinatorOwner>();
        services.AddSingleton<IHostManagerReportService>(static provider =>
            provider.GetRequiredService<HostManagerReportCoordinatorOwner>());
        services.AddSingleton<IOptimizationSoftwareIssueSignalSource>(static provider =>
            provider.GetRequiredService<HostManagerReportCoordinatorOwner>());
        services.AddSingleton<ISoftwareIssueSource, OptimizationReportSoftwareIssueSource>();
        services.AddHostedServiceAlias<HostManagerReportCoordinatorOwner>();

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
        services.AddSingleton<HostManagerSmartCoordinator>();
        services.AddSingleton<IHostManagerSmartCoordinator>(static provider =>
            provider.GetRequiredService<HostManagerSmartCoordinator>());
        services.AddSingleton<IHostManagerProcessEffectValidationScopeControl>(static provider =>
            provider.GetRequiredService<HostManagerSmartCoordinator>());
        services.AddSingleton<IHostManagerMemoryCleanupValidationEvidenceControl>(static provider =>
            provider.GetRequiredService<HostManagerSmartCoordinator>());
        if (startHostedService)
        {
            services.AddHostedServiceAlias<HostManagerSmartCoordinator>();
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
