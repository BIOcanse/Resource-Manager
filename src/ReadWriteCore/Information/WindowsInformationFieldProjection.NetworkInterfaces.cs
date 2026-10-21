using System.Globalization;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static DeviceInformationSnapshot NetworkInterfaces(
        IReadOnlyList<DeviceTopologyNetworkInterfaceInformation>? interfaces, DateTimeOffset? observedAt)
    {
        var devices = new List<DeviceInformationObject>();
        foreach (var row in interfaces ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.DeviceIdentity)) continue;
            DeviceInformationField[] fields = [Text(F.NetworkAllInterfaceGuidReported, row.InterfaceGuid),
                Text(F.NetworkAllInterfaceLuidReported, row.InterfaceLuid?.ToString("X16", CultureInfo.InvariantCulture)),
                Integer(F.NetworkAllInterfaceIndexReported, row.InterfaceIndex),
                Value(F.NetworkAllUnicastAddressReported, row.UnicastAddresses),
                Value(F.NetworkAllRouteReported, row.Routes)];
            if (observedAt is null) fields = fields.Select(static f => f with { Value = null }).ToArray();
            devices.Add(new(row.DeviceIdentity, "network_interface", Array.AsReadOnly(fields)));
        }
        return new(observedAt, devices.AsReadOnly());
    }
}
