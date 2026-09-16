using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Control;
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
            .AddResourceManagerSettings(startupCapabilities)
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

    private static IServiceCollection AddResourceManagerSettings(
        this IServiceCollection services,
        StartupCapabilitySet startupCapabilities)
    {
        services.AddSingleton<IDashboardSettingsStore, JsonDashboardSettingsStore>();
        services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
        // 控制面的持久化与写回路。放在这里而不是监控注册里：
        // 期望状态的存储要 IHostEnvironment，而只读服务图里没有它 ——
        // 那张图本来也不该带着一个会写硬件的后台服务。
        services.AddSingleton<IControlDesiredStateStore, JsonControlDesiredStateStore>();
        services.AddSingleton<IControlPlanExecutor, ControlPlanExecutor>();
        services.AddSingleton<IControlPlane, ControlPlane>();
        // 设定要一直维持：启动后和之后每隔一段重新施加一次。
        //
        // 只在允许运行时写入的档位里注册。只读服务图不该带着一个会写硬件的后台服务 ——
        // 这也是那两个服务图测试守着的东西。
        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            services.AddHostedService<ControlDesiredStateReassertion>();
        }
        services.AddSingleton<DashboardSettingsMigrator>();
        return services;
    }
}
