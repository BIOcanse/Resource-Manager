using BlockUids = ResourceManager.App.StateItems.BlockUids;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerGpuPlacement(
        this IServiceCollection services,
        StartupCapabilitySet startupCapabilities)
    {
        services.AddSingleton<IGpuPlacementPolicyStore, JsonGpuPlacementPolicyStore>();
        services.AddSingleton(ResourceManager.Shared.Packages.PackageRuntimeCatalog.Current);
        services.AddSingleton<IStartupModuleSettingsStore, JsonStartupModuleSettingsStore>();
        services.AddSingleton<IStartupModuleSettingsSubscriptionSource>(provider =>
            (JsonStartupModuleSettingsStore)provider.GetRequiredService<IStartupModuleSettingsStore>());
        services.AddSingleton<IGpuPlacementProcessHistoryStore, JsonGpuPlacementProcessHistoryStore>();
        services.AddSingleton<IPresentGraphicsApiObservationSource>(static provider => provider.GetRequiredService<PresentFrameEtwSource>());
        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
            services.AddBusinessBlockWorker<GpuPlacementProcessHistoryObserver>(BlockUids.GpuPlacementProcessHistoryObserver);
        services.AddSingleton<IWindowsGraphicsPreferenceStore, WindowsGraphicsPreferenceStore>();
        services.AddSingleton<D3d11ProxyShimRuntime>();
        services.AddSingleton<WindowsGpuPlacementInjector>();
        services.AddSingleton<WindowsGpuWindowActionRuntime>();
        services.AddSingleton<WindowsGpuCallbackPreparationRuntime>();
        services.AddSingleton<ExternalGpuResidentObservation>();
        services.AddSingleton<RendererAbsenceCache>();
        services.AddSingleton<WindowsExternalGpuPlacementRuntime>(provider => new(
            provider.GetRequiredService<IHostEnvironment>(), provider.GetRequiredService<ExternalGpuResidentObservation>(),
            provider.GetRequiredService<ILogger<WindowsExternalGpuPlacementRuntime>>(),
            provider.GetRequiredService<IHostManagerRollbackStateStore>(), provider.GetRequiredService<RendererAbsenceCache>()));
        services.AddSingleton<IGpuPlacementCapabilityReader, WindowsGpuPlacementCapabilityReader>();
        services.AddSingleton<IGpuLaunchInterceptionRegistry, WindowsIfeoGpuLaunchInterceptionRegistry>();
        services.AddSingleton<IGpuLaunchExecutionReportStore, JsonGpuLaunchExecutionReportStore>();
        services.AddSingleton<IGpuStartupPlacementResolver, GpuStartupPlacementResolver>();
        services.AddSingleton<IGpuSchedulingAvailability, GpuSchedulingAvailabilityReader>();
        services.AddSingleton<GpuLaunchInterceptionReconciler>();
        if (startupCapabilities.Allows(
                StartupCapability.GpuLaunchInterceptionReconciliation))
        {
            // Report reconciliation failures through the existing business-block diagnostics.
            services.AddSingleton<ILogger<GpuLaunchInterceptionReconciler>>(provider =>
                new BusinessBlockFailureLogger<GpuLaunchInterceptionReconciler>(
                    provider.GetRequiredService<ILoggerFactory>().CreateLogger<GpuLaunchInterceptionReconciler>(),
                    provider.GetRequiredService<IBusinessBlockDiagnostics>(), BlockUids.Program));
            // The serial host turn owns a publication subscription; it does not reconcile before publication.
            services.AddSingleton(new BusinessBlockDiagnosticRegistration(BlockUids.Program));
            services.AddSerialHostedServiceAlias<GpuLaunchInterceptionReconciler>();
        }
        services.AddSingleton<IRunningGpuPlacementActionService, ExternalOnlyRunningGpuPlacementActionService>();
        return services;
    }
}
