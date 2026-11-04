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
        // The item catalog and the target reader are reads of live sources; the performance overlay uses the
        // catalog without persistence, so they are registered in every startup.
        services.AddSingleton<ITargetedRecordingItemCatalog, TargetedRecordingItemCatalog>();
        services.AddSingleton<ITargetedRecordingProcessReader, ResourceBreakdownTargetedRecordingProcessReader>();
        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
        {
            services.AddSingleton<ResourceManagerDatabase>();
            services.AddSingleton<TargetedRecordingProfileService>();
            services.AddSingleton<ITargetedRecordingProfileReader>(static provider =>
                provider.GetRequiredService<TargetedRecordingProfileService>());
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
