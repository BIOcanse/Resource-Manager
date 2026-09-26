using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerDeviceTopology(this IServiceCollection services)
    {
        services.AddSingleton<DeviceIdCatalog>();
        services.AddSingleton<IDeviceTopologyReader, WindowsDeviceTopologyReader>();
        services.AddSingleton<DeviceTopologySemanticComparer>();
        services.AddSingleton<IDeviceTopologySnapshotStore, JsonDeviceTopologySnapshotStore>();
        services.AddSingleton<DeviceTopologySnapshotProvider>();
        services.AddSingleton<IDeviceTopologySnapshotProvider>(static provider =>
            provider.GetRequiredService<DeviceTopologySnapshotProvider>());
        services.AddHostedServiceAlias<DeviceTopologySnapshotProvider>();
        return services;
    }
}
