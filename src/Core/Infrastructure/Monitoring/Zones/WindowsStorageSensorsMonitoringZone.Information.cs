using ResourceManager.App.Domain.DeviceTopology;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed partial class WindowsStorageSensorsMonitoringZone
{
    internal IReadOnlyList<DeviceTopologyStorageInformation>? ReadInformationDeclarations()
        => owner.ReadInformationDeclarations();
}
