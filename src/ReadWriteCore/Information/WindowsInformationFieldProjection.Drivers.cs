using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static DeviceInformationSnapshot Drivers(IReadOnlyList<DeviceTopologyDriverInformation>? rows, DateTimeOffset? observedAt)
        => Frame((rows ?? []).Where(static row => !string.IsNullOrWhiteSpace(row.DeviceInstanceId))
            .GroupBy(static row => row.DeviceIdentity, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1).Select(static group => group.Single())
            .OrderBy(static row => row.DeviceIdentity, StringComparer.Ordinal)
            .Select(static row => new DeviceInformationObject(row.DeviceIdentity, "device_bound_driver",
                [Text(DeviceInformationFieldRegistry.DriverAllProviderDeclared, row.ProviderDeclared),
                 Text(DeviceInformationFieldRegistry.DriverAllDateDeclared, row.DateDeclared),
                 Text(DeviceInformationFieldRegistry.DriverAllVersionDeclared, row.VersionDeclared)])), observedAt);
}
