using ResourceManager.App.Infrastructure.DeviceTopology;

namespace ResourceManager.ReadWrite.Windows.Devices.Topology;

public enum WindowsDeviceTopologyWmiFact { PnpEntities, Bios, ComputerSystem, BaseBoard, UsbControllers, UsbHubs, UsbAssociations }

/// <summary>Seven original stateless topology queries. Construction performs no query.
/// Each selected fact uses the explicit enumeration timeout; no aggregate cache or optional source reads.</summary>
public sealed class WindowsDeviceTopologyWmiFactsReader(TimeSpan queryTimeout,
    Func<string, string, TimeSpan, DeviceTopologyWmiQueryResult>? query = null)
{
    private static readonly (string Scope, string Query)[] Sources = [
        (@"root\CIMV2", "SELECT Name, Caption, Description, Manufacturer, Service, Status, PNPClass, PNPDeviceID, DeviceID, ClassGuid, HardwareID, CompatibleID FROM Win32_PnPEntity"),
        (@"root\CIMV2", "SELECT Manufacturer, SMBIOSBIOSVersion, Version FROM Win32_BIOS"),
        (@"root\CIMV2", "SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber FROM Win32_ComputerSystem"),
        (@"root\CIMV2", "SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard"),
        (@"root\CIMV2", "SELECT Name, Caption, Description, Manufacturer, DeviceID, PNPDeviceID, Status FROM Win32_USBController"),
        (@"root\CIMV2", "SELECT Name, Caption, Description, DeviceID, PNPDeviceID, Status FROM Win32_USBHub"),
        (@"root\CIMV2", "SELECT Antecedent, Dependent FROM Win32_USBControllerDevice")
    ];

    public DeviceTopologyWmiQueryResult Read(WindowsDeviceTopologyWmiFact fact)
    {
        if ((uint)fact >= (uint)Sources.Length) throw new ArgumentOutOfRangeException(nameof(fact));
        var source = Sources[(int)fact];
        return (query ?? DeviceTopologyWmiUtilities.QueryObjects)(source.Scope, source.Query, queryTimeout);
    }
}
