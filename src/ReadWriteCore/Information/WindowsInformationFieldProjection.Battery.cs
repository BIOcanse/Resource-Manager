using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static IReadOnlyList<DeviceInformationField> BatteryDeclarationFields(DeviceTopologyBattery battery)
        => [Text(F.BatteryAllManufacturerDeclared, battery.Manufacturer), Text(F.BatteryAllSerialDeclared, battery.SerialNumber),
            Text(F.BatteryAllChemistryDeclared, battery.Chemistry)];

    public static DeviceInformationSnapshot BatteryDeclarations(IReadOnlyList<DeviceTopologyBattery> batteries, DateTimeOffset? observedAt)
        => Frame(batteries.Select(static battery => new DeviceInformationObject(battery.DeviceIdentity, "battery_device",
            BatteryDeclarationFields(battery))), observedAt);
}
