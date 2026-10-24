using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Infrastructure.LocalSystem;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.ServiceHosting;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerShell(
        this IServiceCollection services,
        string[] args,
        BackendHostEnvironment hostEnvironment)
    {
        services.AddHostedService<ServiceAutoStartSettings>();
        services.AddSingleton<IServiceRecoveryControl, WindowsServiceRecoveryControl>();
        services.AddSingleton<ServiceRecoverySettings>();
        services.AddHostedService(provider => provider.GetRequiredService<ServiceRecoverySettings>());
        if (hostEnvironment.IsWindowsService
            && !args.Any(static arg => arg.Equals(
                "--no-native-ui",
                StringComparison.OrdinalIgnoreCase)))
        {
            services.AddHostedService<NativeUiLaunchHostedService>();
        }

        return services;
    }

    private static IServiceCollection AddResourceManagerSystem(this IServiceCollection services)
    {
        services.AddSingleton<IInteractiveUserSessionBroker, WindowsInteractiveUserSessionBroker>();
        services.AddSingleton<ILocalPathOpener, WindowsExplorerPathOpener>();
        services.AddSingleton<ISystemProcessActionService, WindowsSystemProcessActionService>();
        services.AddSingleton<ILocalSystemStatusProvider, WindowsLocalSystemStatusProvider>();
        return services;
    }
}
