using ResourceManager.App.Infrastructure.DeviceTopology;

namespace ResourceManager.ReadWrite.Windows.Devices.Topology;

public enum WindowsDeviceTopologyWmiFact { PnpEntities, Bios, ComputerSystem, BaseBoard, UsbControllers, UsbHubs, UsbAssociations }

/// <summary>Selected topology queries. Fixed firmware declarations are read once per reader,
/// including absence or query failure. Device and USB facts follow explicit capture demand.</summary>
public sealed class WindowsDeviceTopologyWmiFactsReader
{
    private readonly TimeSpan queryTimeout;
    private readonly Func<string, string, TimeSpan, DeviceTopologyWmiQueryResult> query;
    private readonly Lazy<DeviceTopologyWmiQueryResult>[] fixedFacts;

    public WindowsDeviceTopologyWmiFactsReader(TimeSpan queryTimeout,
        Func<string, string, TimeSpan, DeviceTopologyWmiQueryResult>? query = null)
    {
        this.queryTimeout = queryTimeout;
        this.query = query ?? DeviceTopologyWmiUtilities.QueryObjects;
        fixedFacts = [new(() => QueryFixed(WindowsDeviceTopologyWmiFact.Bios)),
            new(() => QueryFixed(WindowsDeviceTopologyWmiFact.ComputerSystem)),
            new(() => QueryFixed(WindowsDeviceTopologyWmiFact.BaseBoard))];
    }
    private static readonly (string Scope, string Query)[] Sources = [
        (@"root\CIMV2", "SELECT Name, Caption, Description, Manufacturer, Service, Status, PNPClass, PNPDeviceID, DeviceID, ClassGuid, HardwareID, CompatibleID FROM Win32_PnPEntity"),
        (@"root\CIMV2", "SELECT Manufacturer, SMBIOSBIOSVersion, Version FROM Win32_BIOS"),
        (@"root\CIMV2", "SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber FROM Win32_ComputerSystem"),
        (@"root\CIMV2", "SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard"),
        (@"root\CIMV2", "SELECT Name, Caption, Description, Manufacturer, DeviceID, PNPDeviceID, Status FROM Win32_USBController"),
        (@"root\CIMV2", "SELECT Name, Caption, Description, DeviceID, PNPDeviceID, Status FROM Win32_USBHub"),
        (@"root\CIMV2", "SELECT Antecedent, Dependent FROM Win32_USBControllerDevice")
    ];

    [RuntimeReadCache]
    public DeviceTopologyWmiQueryResult Read(WindowsDeviceTopologyWmiFact fact)
    {
        if ((uint)fact >= (uint)Sources.Length) throw new ArgumentOutOfRangeException(nameof(fact));
        if (fact is WindowsDeviceTopologyWmiFact.Bios or WindowsDeviceTopologyWmiFact.ComputerSystem or WindowsDeviceTopologyWmiFact.BaseBoard)
        {
            return fixedFacts[(int)fact - (int)WindowsDeviceTopologyWmiFact.Bios].Value;
        }
        return QueryDevice(fact);
    }

    [RuntimeRead(ReadVolatilityKind.Fixed)]
    private DeviceTopologyWmiQueryResult QueryFixed(WindowsDeviceTopologyWmiFact fact)
    {
        var source = Sources[(int)fact];
        return query(source.Scope, source.Query, queryTimeout);
    }

    [RuntimeRead(ReadVolatilityKind.EventDriven, "Windows device arrival, removal or device-property change")]
    private DeviceTopologyWmiQueryResult QueryDevice(WindowsDeviceTopologyWmiFact fact)
    {
        var source = Sources[(int)fact];
        return query(source.Scope, source.Query, queryTimeout);
    }
}
