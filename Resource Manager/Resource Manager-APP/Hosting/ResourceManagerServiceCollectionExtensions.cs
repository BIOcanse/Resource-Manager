using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Settings;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    public static IServiceCollection AddResourceManagerApp(
        this IServiceCollection services,
        string[] args)
        => services.AddResourceManagerApp(
            args,
            StartupProfileCompiler.Compile(args));

    public static IServiceCollection AddResourceManagerApp(
        this IServiceCollection services,
        string[] args,
        StartupCapabilitySet startupCapabilities)
        => services.AddResourceManagerApp(
            args,
            startupCapabilities,
            BackendHostEnvironment.Interactive);

    public static IServiceCollection AddResourceManagerApp(
        this IServiceCollection services,
        string[] args,
        StartupCapabilitySet startupCapabilities,
        BackendHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(startupCapabilities);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        services.AddSingleton(startupCapabilities);
        services.AddSingleton(hostEnvironment);
        services.AddSingleton(new RuntimeExecutionCapabilityPolicy(
            startupCapabilities.Allows(StartupCapability.OptimizationRuntime),
            startupCapabilities.Allows(
                StartupCapability.GpuLaunchInterceptionReconciliation),
            startupCapabilities.Allows(
                StartupCapability.PublicServiceCoordination)));
        services.AddSingleton(new RuntimePersistenceCapabilityPolicy(
            startupCapabilities.Allows(StartupCapability.MutablePersistence)));
        services.AddHostedServiceAlias<RuntimeSpecializationCoordinator>();

        services
            .AddResourceManagerRuntimeSpecialization(startupCapabilities)
            .AddResourceManagerPersistence(startupCapabilities)
            .AddResourceManagerShell(args, hostEnvironment)
            .AddResourceManagerSettings()
            .AddResourceManagerBrowserRuntimes()
            .AddResourceManagerPublicServices(startupCapabilities)
            .AddResourceManagerExternalInvocation()
            .AddResourceManagerMonitoring(startupCapabilities)
            .AddResourceManagerAdaptation()
            .AddResourceManagerSoftware()
            .AddResourceManagerMigration()
            .AddResourceManagerComponentsAndTasks()
            .AddResourceManagerGpuPlacement(startupCapabilities)
            .AddResourceManagerSharedResources(startupCapabilities)
            .AddResourceManagerSystemHealth()
            .AddResourceManagerOptimization(startupCapabilities)
            .AddResourceManagerDeviceTopology()
            .AddResourceManagerRuntimeIdentity()
            .AddResourceManagerDependencies();

        return services;
    }

    private static IServiceCollection AddResourceManagerSettings(this IServiceCollection services)
    {
        services.AddSingleton<IDashboardSettingsStore, JsonDashboardSettingsStore>();
        services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
        services.AddSingleton<DashboardSettingsMigrator>();
        return services;
    }
}
