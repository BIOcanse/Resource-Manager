using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceTopologyUsbConnectorGrouperTests
{
    [Fact]
    public void GroupUserConnectablePorts_MergesCompanionHubViews()
    {
        const string hubPath = @"\\?\USB#ROOT_HUB30#A";
        var usb2 = CreatePort(
            hubPath,
            1,
            userConnectable: true,
            supportsUsb30: false,
            companions: [new DeviceTopologyUsbCompanionPortInfo(0, 2, @"\??\USB#ROOT_HUB30#A")]);
        var usb3 = CreatePort(
            hubPath,
            2,
            userConnectable: true,
            supportsUsb30: true,
            companions: [new DeviceTopologyUsbCompanionPortInfo(0, 1, @"\??\USB#ROOT_HUB30#A")]);

        var group = Assert.Single(DeviceTopologyUsbConnectorGrouper.GroupUserConnectablePorts([usb2, usb3]));

        Assert.Equal(2, group.Ports.Count);
        Assert.Same(usb3, group.Representative);
    }

    [Fact]
    public void GroupUserConnectablePorts_PrefersConnectedViewAndOmitsInternalPorts()
    {
        var emptyUsb3 = CreatePort(
            @"\\?\USB#HUB#A",
            3,
            userConnectable: true,
            supportsUsb30: true,
            companions: [new DeviceTopologyUsbCompanionPortInfo(0, 4, @"\??\USB#HUB#A")]);
        var connectedUsb2 = CreatePort(
            @"\\?\USB#HUB#A",
            4,
            userConnectable: true,
            supportsUsb30: false,
            connected: true,
            companions: [new DeviceTopologyUsbCompanionPortInfo(0, 3, @"\??\USB#HUB#A")]);
        var internalPort = CreatePort(
            @"\\?\USB#HUB#B",
            1,
            userConnectable: false,
            supportsUsb30: true);

        var group = Assert.Single(DeviceTopologyUsbConnectorGrouper.GroupUserConnectablePorts(
            [emptyUsb3, connectedUsb2, internalPort]));

        Assert.Same(connectedUsb2, group.Representative);
        Assert.DoesNotContain(internalPort, group.Ports);
    }

    [Fact]
    public void CreatePortIdentity_NormalizesWin32AndNtPrefixes()
    {
        var win32 = DeviceTopologyUsbConnectorGrouper.CreatePortIdentity(@"\\?\USB#HUB#A", 7);
        var nt = DeviceTopologyUsbConnectorGrouper.CreatePortIdentity(@"\??\USB#HUB#A", 7);

        Assert.Equal(win32, nt, ignoreCase: true);
    }

    private static DeviceTopologyUsbPort CreatePort(
        string hubPath,
        uint portNumber,
        bool userConnectable,
        bool supportsUsb30,
        bool connected = false,
        IReadOnlyList<DeviceTopologyUsbCompanionPortInfo>? companions = null)
    {
        return new DeviceTopologyUsbPort(
            HubDevicePath: hubPath,
            PortNumber: portNumber,
            DeviceConnected: connected,
            ConnectionStatus: connected ? "已连接" : "未连接",
            NegotiatedSpeed: connected ? "USB 2.0 High-Speed / 480Mbps" : "未连接",
            VendorId: 0,
            ProductId: 0,
            DeviceAddress: 0,
            DeviceIsHub: false,
            DriverKeyName: null,
            ConnectorProperties: new DeviceTopologyUsbConnectorProperties(
                PortIsUserConnectable: userConnectable,
                PortIsDebugCapable: false,
                PortHasMultipleCompanions: (companions?.Count ?? 0) > 0,
                PortConnectorIsTypeC: false,
                CompanionPorts: companions ?? []),
            Capability: new DeviceTopologyUsbPortCapability(
                SupportsUsb11: true,
                SupportsUsb20: true,
                SupportsUsb30: supportsUsb30,
                OperatingAtSuperSpeedOrHigher: false,
                SuperSpeedCapableOrHigher: supportsUsb30,
                OperatingAtSuperSpeedPlusOrHigher: false,
                SuperSpeedPlusCapableOrHigher: false),
            Descriptor: new DeviceTopologyUsbDescriptor(
                DeviceSpecification: "--",
                DeviceRevision: "--",
                DeviceClass: "--",
                ManufacturerName: null,
                ProductName: null,
                SerialNumber: null,
                InterfaceProtocols: [],
                Endpoints: [],
                HidDescriptors: [],
                CameraModes: []));
    }
}
