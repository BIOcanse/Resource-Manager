using BlockUids = ResourceManager.App.StateItems.BlockUids;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Settings;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Updates;

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
        services.ConfigureHttpJsonOptions(options =>
            ResourceManager.App.Endpoints.ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
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
        services.AddHostedServiceAlias<RuntimeSpecializationCoordinator>(BlockUids.RuntimeSpecializationCoordinator);
        services.AddHttpClient();
        services.AddHttpClient(ProductUpdateSource.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton(TimeProvider.System);
        // One notification owner for every producer; the subscription channel and GET read it.
        services.AddSingleton<ResourceManager.App.Infrastructure.Notifications.NotificationOwner>();
        services.AddSingleton<ResourceManager.App.Application.Notifications.INotificationOwner>(provider =>
            provider.GetRequiredService<ResourceManager.App.Infrastructure.Notifications.NotificationOwner>());
        services.AddSingleton<ProductVersionCatalogService>();
        services.AddSingleton<ProductUpdateCoordinator>();
        if (hostEnvironment.IsWindowsService && startupCapabilities.Allows(StartupCapability.MutablePersistence))
        {
            services.AddSingleton<IProductPackageUsage, ProductPackageUsage>();
            services.AddSingleton<IInternetConnectivity, WindowsInternetConnectivity>();
            services.AddBusinessBlockHostedService<AutomaticProductUpdateService>(BlockUids.AutomaticProductUpdateService);
        }

        RegisterHostedBusinessBlock<ResourceManager.App.Infrastructure.DeviceTopology.Snapshots.DeviceTopologySnapshotProvider>(services,
            BlockUids.DeviceTopologySnapshotProvider);

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
            .AddResourceManagerFileChangeTracking()
            .AddResourceManagerSoftware()
            .AddResourceManagerComponentsAndTasks()
            .AddResourceManagerGpuPlacement(startupCapabilities)
            .AddResourceManagerSystemHealth()
            .AddResourceManagerOptimization(startupCapabilities)
            .AddResourceManagerDeviceTopology()
            .AddResourceManagerRuntimeIdentity()
            .AddResourceManagerDependencies();

        services.AddBackendDecisionStateBindings();
        services.AddEtwBusinessBlocks();
        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners)) services.AddHostMemoryProtection();

        return services;
    }

    private static IServiceCollection AddResourceManagerSettings(
        this IServiceCollection services,
        StartupCapabilitySet startupCapabilities)
    {
        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
            services.AddSingleton<LegacyDashboardFileMigration>();
        services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
        services.AddSingleton<IAppSettingsSubscriptionSource>(provider =>
            (JsonAppSettingsStore)provider.GetRequiredService<IAppSettingsStore>());
        // 控制面的持久化与写回路。放在这里而不是监控注册里：
        // 期望状态的存储要 IHostEnvironment，而只读服务图里没有它 ——
        // 那张图本来也不该带着一个会写硬件的后台服务。
        services.AddSingleton<IControlDesiredStateStore, JsonControlDesiredStateStore>();
        // 统一写入层：认实例 → 选驱动 → 适配单位 → 下发 → 归一回执。只管写。
        services.AddSingleton<IControlWriteLayer, ControlWriteLayer>();
        services.AddSingleton<IControlPlane, ControlPlane>();
        // 配置：用户攒下来的几套方案。存一份不动硬件，只有应用才动。
        // 超频免责声明的同意状态。厂商（Intel IGCL）硬性要求用户先明确接受，
        // 我们不替他默认接受。
        services.AddSingleton<IControlOverclockConsent, JsonControlOverclockConsent>();
        // 安全模式是我们自己那道闸：默认只准往不增加硬件应力的方向调。
        // 和上面那个 Intel 接口豁免是两件事，互不替代。
        services.AddSingleton<IControlAccessLevel, JsonControlAccessLevel>();
        services.AddSingleton<IControlNoticeAcknowledgement, JsonControlNoticeAcknowledgement>();
        services.AddSingleton<IControlPresetStore, JsonControlPresetStore>();
        services.AddSingleton<IControlPresets, ControlPresets>();
        // 登记表：见过的设备只增不减，配置挂在它上面，拔掉卡也不会变成孤儿。
        services.AddSingleton<IControlInstanceRegistry, JsonControlInstanceRegistry>();
        // 目录就绪后应用保存的选择一次；之后按设置间隔只维持成功应用的项。
        //
        // 只在允许运行时写入的档位里注册。只读服务图不该带着一个会写硬件的后台服务 ——
        // 这也是那两个服务图测试守着的东西。
        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            services.AddBusinessBlockHostedService<ControlDesiredStateReassertion>(BlockUids.ControlDesiredStateReassertion);
            /*
             * 给软件风扇曲线喂温度。
             *
             * **和上面那条同一个闸**：它自己不写硬件，但它驱动的那个曲线引擎写。
             * 只读服务图里不该有这条 —— 那正是服务图测试守着的东西。
             *
             * 这台机器用不上的话它一次都不推（核心会在 describe 里说要不要），
             * 所以注册它不等于一定会跑起来。
             */
            services.TryAddSingleton<FanCurveTemperatureFeed>();
            RegisterHostedBusinessBlock<FanCurveTemperatureFeed>(services, BlockUids.FanCurveTemperatureFeed);
            // 实际状态的采样。和写入层分开的另一条路：它按自己的节奏读，
            // 结果原子替换进当前值，订阅端只取当前值、不触发采样。
            services.AddSingleton<ControlActualStateReader>();
            services.AddSingleton<IControlActualStateOwner>(static provider =>
                provider.GetRequiredService<ControlActualStateReader>());
            services.AddBusinessBlockHostedService<ControlActualStateReader>(BlockUids.ControlActualStateReader,
                static provider => provider.GetRequiredService<ControlActualStateReader>());
        }
        else
        {
            // 只读服务图里没有采样那条路，当前值就一直是空的 ——
            // 订阅端照样能问，只是问到的是"还没采过"。
            services.AddSingleton<IControlActualStateOwner, EmptyControlActualStateOwner>();
        }
        return services;
    }
}
