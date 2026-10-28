using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerDeviceTopology(this IServiceCollection services)
    {
        services.AddSingleton<DeviceIdCatalog>();
        services.AddSingleton<IDeviceTopologyReader, WindowsDeviceTopologyReader>();
        services.AddSingleton<IAcpiFirmwareTableReader>(static provider =>
        {
            var logger = provider.GetRequiredService<ILogger<WindowsAcpiFirmwareTableReader>>();
            return new WindowsAcpiFirmwareTableReader((message, arguments) => logger.LogDebug(message, arguments));
        });
        services.AddSingleton<DeviceTopologySemanticComparer>();
        services.AddSingleton<IDeviceTopologySnapshotStore, JsonDeviceTopologySnapshotStore>();
        services.AddSingleton<DeviceTopologySnapshotProvider>(static provider => new(
            provider.GetRequiredService<IDeviceTopologyReader>(),
            provider.GetRequiredService<IDeviceTopologySnapshotStore>(),
            provider.GetRequiredService<DeviceTopologySemanticComparer>(),
            provider.GetRequiredService<HostManagerSamplingSubscriptionOwner>(),
            provider.GetRequiredService<ILogger<DeviceTopologySnapshotProvider>>(),
            provider.GetRequiredService<IAcpiFirmwareTableReader>(),
            provider.GetRequiredService<ResourceManager.App.Infrastructure.Adaptation.ResourceManagerLedgerOwner>(),
            provider.GetService<ResourceManager.App.Infrastructure.Software.WindowsInstalledSoftwareInventory>(),
            provider.GetService<ResourceManager.ReadWrite.Windows.Services.WindowsServiceInformationOwner>(),
            provider.GetService<ResourceManager.ReadWrite.Windows.SystemInformation.WindowsOperatingSystemInformationOwner>(),
            provider.GetService<ResourceManager.App.Infrastructure.Monitoring.NvidiaNvapiMonitoringZone>(),
            stateAuthority: provider.GetRequiredService<ResourceManager.Shared.BusinessBlocks.State.StateAuthority>()));
        services.AddSingleton<IDeviceTopologySnapshotProvider>(static provider =>
            provider.GetRequiredService<DeviceTopologySnapshotProvider>());
        services.AddHostedServiceAlias<DeviceTopologySnapshotProvider>();
        return services;
    }
}
