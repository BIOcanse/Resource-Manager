using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.App.Infrastructure.Persistence.Legacy;
using ResourceManager.App.Application.Indexing;
using ResourceManager.App.Infrastructure.Indexing;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerPersistence(
        this IServiceCollection services,
        StartupCapabilitySet startupCapabilities)
    {
        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
        {
            services.AddSingleton<ResourceManagerDatabase>();
            services.AddSingleton<SqliteSoftwareFileIndex>();
            services.AddSingleton<ISoftwareFileIndex>(static provider =>
                provider.GetRequiredService<SqliteSoftwareFileIndex>());
            services.AddSingleton<ISoftwareFileIndexRefresher>(static provider =>
                provider.GetRequiredService<SqliteSoftwareFileIndex>());
            services.AddSingleton<SqlitePortableSoftwareRegistry>();
            services.AddSingleton<IPortableSoftwareRegistry>(static provider =>
                provider.GetRequiredService<SqlitePortableSoftwareRegistry>());
            services.AddHostedService<ResourceManagerDatabaseInitializer>();
            services.AddHostedServiceAlias<SqlitePortableSoftwareRegistry>();
        }
        else
        {
            services.AddSingleton<IPortableSoftwareRegistry,
                StartupDisabledPortableSoftwareRegistry>();
        }

        if (startupCapabilities.Allows(StartupCapability.LegacyPersistenceImport))
        {
            services.AddHostedService<SqliteLegacyDataImportHostedService>();
        }
        return services;
    }
}
