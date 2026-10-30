using BlockUids = ResourceManager.App.StateItems.BlockUids;
using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.App.Application.Indexing;
using ResourceManager.App.Infrastructure.Indexing;
using ResourceManager.App.Application.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

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
            services.AddSingleton<TargetedRecordingStore>();
            services.AddSingleton<TargetedRecordingService>();
            services.AddSerialHostedServiceAlias<TargetedRecordingService>();
            services.AddSingleton<SqliteSoftwareFileIndex>();
            services.AddSingleton<ISoftwareFileIndex>(static provider =>
                provider.GetRequiredService<SqliteSoftwareFileIndex>());
            services.AddSingleton<ISoftwareFileIndexRefresher>(static provider =>
                provider.GetRequiredService<SqliteSoftwareFileIndex>());
            services.AddSingleton<SqlitePortableSoftwareRegistry>();
            services.AddSingleton<IPortableSoftwareRegistry>(static provider =>
                provider.GetRequiredService<SqlitePortableSoftwareRegistry>());
            services.AddSerialHostedService<ResourceManagerDatabaseInitializer>();
            services.AddHostedServiceAlias<SqlitePortableSoftwareRegistry>(BlockUids.SqlitePortableSoftwareRegistry,
                static registry => registry.ExecuteTask, forwardRunFailureToHost: true);
        }
        else
        {
            services.AddSingleton<IPortableSoftwareRegistry,
                StartupDisabledPortableSoftwareRegistry>();
        }
        return services;
    }
}
