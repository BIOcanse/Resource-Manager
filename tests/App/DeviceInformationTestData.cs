using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;

namespace Resource_Manager_APP.Tests;

internal static class DeviceInformationTestData
{
    internal static DeviceInformationField Field(DeviceInformationSnapshot snapshot, string identity, string name)
        => Assert.Single(Assert.Single(snapshot.Devices, device => device.DeviceIdentity == identity).Fields,
            field => field.Definition.Name == name);

    internal static DeviceTopologySnapshot Snapshot(params DeviceTopologyPort[] ports) => new(
        DateTimeOffset.UtcNow, new("Machine Vendor", "Machine Model", "Brand", "Logo", "Bios Version", "Board Vendor", "Board Model"), ports, []);

    internal static FirmwareMemoryModule Module(string identity) => new(identity, ulong.MaxValue,
        "Slot 1", "Vendor", "Part", "Same Serial", "DDR5", 5600, 5200, 64, 64, 1.1);

    internal static readonly BackendMessage Message = BackendMessage.Create(
        BackendMessageDomains.DeviceTopology, BackendMessageCodes.DeviceTopology.SourcePnpEnumeration);

    internal static DeviceTopologyPort Port(string id) => new(
        Id: "port:" + id, IsPhysicalConnector: false, DisplayName: "Device", ConnectorKind: DeviceConnectorKinds.Generic,
        BusKind: DeviceBusKinds.Pci, HardwareKind: null, Protocol: null, Speed: null, DeviceId: id, PnpClass: null,
        Manufacturer: "Database display name", Service: null, Status: null, Confidence: Message, Source: Message,
        UpstreamDeviceId: null, UpstreamDisplayName: null, TopologyPath: Message, NativeParentDeviceId: null,
        NativeParentDisplayName: null, LocationInfo: null, LocationPaths: [], ClassGuid: null, Display: null,
        Network: null, IdResolution: null, AdvancedInterconnect: null, Usb: null, HardwareIds: [], CompatibleIds: []);
}
