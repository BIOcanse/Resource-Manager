using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Migration;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.Operations.Effects;
using ResourceManager.App.Infrastructure.PublicServices;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerRuntimeSpecialization(
        this IServiceCollection services,
        StartupCapabilitySet startupCapabilities)
    {
        services.AddSingleton(new HostManagerProfileSource(
            "ResourceManager.Configuration.HostManager.default.json",
            "Configuration/HostManager/default.json"));
        services.AddSingleton<StrictHostManagerProfileLoader>();
        services.AddSingleton<HostManagerPlanCompiler>();
        services.AddSingleton<HostManagerRuntimeIdentity>();
        services.AddSingleton<HostManagerDeploymentState>();
        services.AddSingleton<IHostManagerDeploymentState>(static provider =>
            provider.GetRequiredService<HostManagerDeploymentState>());
        services.AddSingleton<DashboardMonitoringCatalogState>();
        services.AddSingleton(CpuBaselineRatioCatalog.LoadEmbedded());
        services.AddSingleton<RuntimePlanCompiler>();
        services.AddSingleton<RuntimePlanProvider>();
        services.AddSingleton<IRuntimePlanProvider>(static provider => provider.GetRequiredService<RuntimePlanProvider>());
        services.AddSingleton<HostManagerSmartCoordinatorRuntime>();
        services.AddSingleton<HostManagerPlacementCoordinatorRuntime>();
        services.AddSingleton<HostManagerMemoryCleanupRuntime>();
        services.AddSingleton<HostManagerTransactionJournalDeploymentRuntime>();
        services.AddSingleton<HostManagerAppliedOwnershipDeploymentRuntime>();
        services.AddSingleton<HostManagerSamplingSubscriptionRuntime>();
        services.AddSingleton<HostManagerMetricSnapshotRuntime>();
        services.AddSingleton<
            HostManagerMetricSnapshotCatalogTopologySource>();
        services.AddSingleton<HostManagerPortableSoftwareRegistryRuntime>();
        services.AddSingleton<HostManagerSoftwareIdentityRuntime>();
        services.AddSingleton<HostManagerReportCoordinatorRuntime>();
        services.AddSingleton<HostManagerFileQueryRuntime>();
        services.AddSingleton<HostManagerPublicServiceCoordinatorRuntime>();
        services.AddSingleton<HostManagerDisplayCoordinatorRuntime>();
        services.AddSingleton<HostManagerOperationCoordinatorRuntime>();
        services.AddSingleton<HostManagerAdapterPrivateResourceRuntime>();
        services.AddSingleton<HostManagerProcessPolicyExecutorRuntime>();
        services.AddSingleton<HostManagerPdhCollectorRuntime>();
        services.AddSingleton<RuntimeSpecializationCoordinator>();
        services.AddSingleton<IRuntimeSpecializationCoordinator>(static provider => provider.GetRequiredService<RuntimeSpecializationCoordinator>());
        services.AddSingleton<HostManagerSamplingSubscriptionOwner>();
        services.AddHostedServiceAlias<HostManagerSamplingSubscriptionOwner>();
        services.AddSingleton<HostManagerMetricSnapshotOwner>();
        services.AddHostedServiceAlias<HostManagerMetricSnapshotOwner>();
        services.AddSingleton<HostManagerSoftwareIdentityOwner>();
        services.AddHostedServiceAlias<HostManagerSoftwareIdentityOwner>();
        services.AddSingleton<HostManagerFileQueryOwner>();
        services.AddSingleton<INativeFileQueryLeaseSource>(static provider =>
            provider.GetRequiredService<HostManagerFileQueryOwner>());
        services.AddHostedServiceAlias<HostManagerFileQueryOwner>();
        services.AddSingleton<HostManagerDisplayCoordinatorOwner>();
        services.AddHostedServiceAlias<HostManagerDisplayCoordinatorOwner>();
        if (startupCapabilities.Allows(StartupCapability.PublicServiceCoordination))
        {
            services.AddSingleton<HostManagerPublicServiceCoordinatorOwner>();
            services.AddHostedServiceAlias<HostManagerPublicServiceCoordinatorOwner>();
        }
        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            services.AddSingleton<IHostManagerOperationEffectExecutor>(static provider =>
                new ComponentOperationEffectExecutor(
                    provider.GetRequiredService<IComponentManager>()));
            services.AddSingleton<IHostManagerOperationEffectExecutor>(static provider =>
                ComponentOperationEffectExecutor.ForInstall(
                    provider.GetRequiredService<IComponentManager>()));
            services.AddSingleton<IHostManagerOperationEffectExecutor>(static provider =>
                new DependencyOperationEffectExecutor(
                    provider.GetRequiredService<IOptionalDependencyManager>(),
                    launchInstaller: false));
            services.AddSingleton<IHostManagerOperationEffectExecutor>(static provider =>
                new DependencyOperationEffectExecutor(
                    provider.GetRequiredService<IOptionalDependencyManager>(),
                    launchInstaller: true));
            services.AddSingleton<IHostManagerOperationEffectExecutor>(static provider =>
                new SoftwareOperationEffectExecutor(
                    provider.GetRequiredService<ISoftwareOperationManager>()));
            services.AddSingleton<IHostManagerOperationEffectExecutor>(static provider =>
                new MigrationOperationEffectExecutor(
                    provider.GetRequiredService<ISoftwareDataMigrationManager>(),
                    restore: false));
            services.AddSingleton<IHostManagerOperationEffectExecutor>(static provider =>
                new MigrationOperationEffectExecutor(
                    provider.GetRequiredService<ISoftwareDataMigrationManager>(),
                    restore: true));
            services.AddSingleton<IHostManagerOperationEffectExecutor>(static provider =>
                new DiscoveryOperationEffectExecutor(
                    provider.GetRequiredService<ISoftwareDataDiscoveryManager>()));
            services.AddSingleton<HostManagerOperationEffectRouter>(static provider =>
                new HostManagerOperationEffectRouter(
                    provider.GetServices<IHostManagerOperationEffectExecutor>()));
            services.AddSingleton<HostManagerOperationCoordinatorOwner>();
            services.AddSingleton<IHostManagerOperationCommandService>(static provider =>
                provider.GetRequiredService<HostManagerOperationCoordinatorOwner>());
            services.AddSingleton<IHostManagerOperationQueryService>(static provider =>
                provider.GetRequiredService<HostManagerOperationCoordinatorOwner>());
            services.AddHostedServiceAlias<HostManagerOperationCoordinatorOwner>();
        }
        return services;
    }
}
