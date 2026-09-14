using ResourceManager.App.Application.Migration;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Infrastructure.Migration;
using ResourceManager.App.Infrastructure.Migration.Etw;
using ResourceManager.App.Infrastructure.Operations;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerMigration(this IServiceCollection services)
    {
        services.AddSingleton<ISoftwareDataMigrationManager, SoftwareDataMigrationManager>();
        services.AddSingleton<ISoftwareRootResolver, SoftwareRootResolver>();
        services.AddSingleton<IFirstRunFileTraceFactory, EtwFirstRunFileTraceFactory>();
        services.AddSingleton<ISoftwareDataDiscoveryManager, SoftwareDataDiscoveryManager>();
        services.AddSingleton<IFileChangeTracker, FileSystemChangeTracker>();
        return services;
    }
}
