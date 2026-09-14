using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerGpuPlacement(
        this IServiceCollection services,
        StartupCapabilitySet startupCapabilities)
    {
        services.AddSingleton<IGpuPlacementPolicyStore, JsonGpuPlacementPolicyStore>();
        services.AddSingleton<IGpuGraphicsApiDetector, WindowsGpuGraphicsApiDetector>();
        services.AddSingleton<IGpuPlacementProcessHistoryStore, JsonGpuPlacementProcessHistoryStore>();
        services.AddSingleton<IWindowsGraphicsPreferenceStore, WindowsGraphicsPreferenceStore>();
        services.AddSingleton<D3d11ProxyShimRuntime>();
        services.AddSingleton<WindowsGpuPlacementInjector>();
        services.AddSingleton<WindowsGpuWindowActionRuntime>();
        services.AddSingleton<WindowsGpuCallbackPreparationRuntime>();
        services.AddSingleton<IGpuPlacementCapabilityReader, WindowsGpuPlacementCapabilityReader>();
        services.AddSingleton<IGpuLaunchInterceptionRegistry, WindowsIfeoGpuLaunchInterceptionRegistry>();
        services.AddSingleton<IGpuLaunchExecutionReportStore, JsonGpuLaunchExecutionReportStore>();
        services.AddSingleton<IGpuStartupPlacementResolver, GpuStartupPlacementResolver>();
        if (startupCapabilities.Allows(
                StartupCapability.GpuLaunchInterceptionReconciliation))
        {
            services.AddHostedService<GpuLaunchInterceptionReconciler>();
        }
        services.AddSingleton<IRunningGpuPlacementActionService, WindowsRunningGpuPlacementActionService>();
        services.AddSingleton<LegacyGpuPreferenceActionRestorer>();
        return services;
    }
}
