using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.PublicResources;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerSharedResources(
        this IServiceCollection services,
        StartupCapabilitySet startupCapabilities)
    {
        services.AddSingleton<SharedMemoryPublicResourceBroker>();
        services.AddSingleton<ISharedResourceBroker>(serviceProvider =>
            serviceProvider.GetRequiredService<SharedMemoryPublicResourceBroker>());
        services.AddSingleton<IPublicResourceDirectoryQueries>(serviceProvider =>
            serviceProvider.GetRequiredService<SharedMemoryPublicResourceBroker>());
        if (startupCapabilities.Allows(StartupCapability.SharedResourceOwnership))
        {
            services.AddSingleton<IHostPublicResourceCapability>(serviceProvider =>
                serviceProvider.GetRequiredService<SharedMemoryPublicResourceBroker>());
        }
        else
        {
            services.AddSingleton<IHostPublicResourceCapability,
                StartupDisabledHostPublicResourceCapability>();
        }
        services.AddSingleton<IHostPublicResourceSelfManager>(serviceProvider =>
            serviceProvider.GetRequiredService<SharedMemoryPublicResourceBroker>());
        services.AddSingleton<ISharedResourceSubscriptionMaintenance>(serviceProvider =>
            serviceProvider.GetRequiredService<SharedMemoryPublicResourceBroker>());
        if (startupCapabilities.Allows(StartupCapability.SharedResourceOwnership))
        {
            services.AddSingleton<IHostedService>(serviceProvider =>
                serviceProvider.GetRequiredService<SharedMemoryPublicResourceBroker>());
            services.AddHostedService<SharedResourceSubscriptionMaintenanceService>();
        }
        return services;
    }
}
