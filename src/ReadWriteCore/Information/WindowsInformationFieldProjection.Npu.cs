using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static DeviceInformationSnapshot Npus(IReadOnlyList<DeviceTopologyNpuInformation>? rows, DateTimeOffset? observedAt)
        => Frame((rows ?? []).Where(static row => !string.IsNullOrWhiteSpace(row.DeviceIdentity))
            .GroupBy(static row => row.DeviceIdentity, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1).Select(static group => group.Single())
            .OrderBy(static row => row.DeviceIdentity, StringComparer.Ordinal)
            .Select(static row => new DeviceInformationObject(row.DeviceIdentity, "npu",
                [Text(DeviceInformationFieldRegistry.NpuAllDeviceIdDeclared, row.PnpInstanceId),
                 Text(DeviceInformationFieldRegistry.NpuAllName, row.NameDeclared),
                 Text(DeviceInformationFieldRegistry.NpuAllDriverVersionDeclared, row.DriverVersionDeclared),
                 Text(DeviceInformationFieldRegistry.NpuAllLocationInformationReported, row.LocationInformationReported),
                 Value(DeviceInformationFieldRegistry.NpuAllLocationPathsDeclared, row.LocationPathsDeclared)])), observedAt);
}
