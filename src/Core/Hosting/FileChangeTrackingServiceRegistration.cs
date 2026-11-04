using ResourceManager.App.Application.Operations;
using ResourceManager.App.Infrastructure.Operations;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerFileChangeTracking(this IServiceCollection services)
    {
        services.AddSingleton<IFileChangeTracker, FileSystemChangeTracker>();
        return services;
    }
}
