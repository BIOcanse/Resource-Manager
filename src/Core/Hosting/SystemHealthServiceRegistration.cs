using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Infrastructure.SystemHealth.Power;
using ResourceManager.App.Infrastructure.SystemHealth.Storage;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerSystemHealth(this IServiceCollection services)
    {
        services.AddSingleton<IPowerProfileReader, WindowsPowerProfileReader>();
        services.AddSingleton<IStorageHealthReader, WindowsStorageHealthReader>();
        return services;
    }
}
