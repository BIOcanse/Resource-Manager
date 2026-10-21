using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public static partial class DeviceTopologyInformationProjection
{
    private static IReadOnlyList<DeviceInformationField> PciFields(DeviceTopologyPort port)
        => WindowsInformationFieldProjection.Pci(port);
}
