using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsDeviceTopologyReaderTests
{
    [Theory]
    [InlineData("USB4 Host Router", DeviceBusKinds.Usb4, null)]
    [InlineData("Thunderbolt 4 Controller", DeviceBusKinds.Thunderbolt, null)]
    [InlineData("USB 3.2 xHCI Controller", DeviceBusKinds.Usb, null)]
    [InlineData("USB4 Dock 40 Gbps", DeviceBusKinds.Usb4, "40Gbps")]
    [InlineData("USB Ethernet 2.5Gbps", DeviceBusKinds.Usb, null)]
    [InlineData("Network Adapter 40Gbps", DeviceBusKinds.Network, null)]
    public void ResolveSpeed_RequiresExplicitApplicableRateEvidence(
        string searchText,
        string busKind,
        string? expected)
    {
        Assert.Equal(expected, WindowsDeviceTopologyReader.ResolveSpeed(searchText, busKind));
    }

    [Fact]
    public void RefineUsbConnectorKind_UsesConnectorPropertiesInsteadOfUsbBusGuess()
    {
        Assert.Equal(
            DeviceConnectorKinds.Generic,
            WindowsDeviceTopologyReader.RefineUsbConnectorKind(DeviceConnectorKinds.Generic, null));
        Assert.Equal(
            DeviceConnectorKinds.UsbA,
            WindowsDeviceTopologyReader.RefineUsbConnectorKind(
                DeviceConnectorKinds.Generic,
                Connector(userConnectable: true, typeC: false)));
        Assert.Equal(
            DeviceConnectorKinds.UsbC,
            WindowsDeviceTopologyReader.RefineUsbConnectorKind(
                DeviceConnectorKinds.Generic,
                Connector(userConnectable: true, typeC: true)));
        Assert.Equal(
            DeviceConnectorKinds.Generic,
            WindowsDeviceTopologyReader.RefineUsbConnectorKind(
                DeviceConnectorKinds.UsbA,
                Connector(userConnectable: false, typeC: false)));
    }

    [Theory]
    [InlineData(0u, 0, null)]
    [InlineData(2u, 0, null)]
    [InlineData(1u, 0, "USB Low-Speed / 1.5Mbps")]
    [InlineData(1u, 2, "USB 2.0 High-Speed / 480Mbps")]
    public void DescribeNegotiatedUsbSpeed_RequiresConnectedState(uint status, byte speed, string? expected)
    {
        Assert.Equal(expected, WindowsUsbHubIoctlReader.DescribeNegotiatedUsbSpeed(status, speed, null));
    }

    [Fact]
    public void DescribeMaximumUsbSpeed_UsesHighestPhysicalPortCapability()
    {
        var usb2 = new DeviceTopologyUsbPortCapability(
            SupportsUsb11: true,
            SupportsUsb20: true,
            SupportsUsb30: false,
            OperatingAtSuperSpeedOrHigher: false,
            SuperSpeedCapableOrHigher: false,
            OperatingAtSuperSpeedPlusOrHigher: false,
            SuperSpeedPlusCapableOrHigher: false);
        var usb10Gbps = usb2 with
        {
            SupportsUsb30 = true,
            SuperSpeedCapableOrHigher = true,
            SuperSpeedPlusCapableOrHigher = true
        };

        Assert.Equal(
            "USB SuperSpeedPlus / 10Gbps+",
            WindowsUsbHubIoctlReader.DescribeMaximumUsbSpeed([usb2, usb10Gbps]));
        Assert.Null(
            WindowsUsbHubIoctlReader.DescribeMaximumUsbSpeed([]));
    }

    [Fact]
    public void ResolvePreferredDeviceName_ReplacesOnlyGenericNames()
    {
        Assert.Equal(
            "Catalog Product",
            WindowsDeviceTopologyReader.ResolvePreferredDeviceName(
                "USB Composite Device",
                @"USB\VID_1234&PID_5678",
                null,
                "Catalog Product"));
        Assert.Equal(
            "Descriptor Product",
            WindowsDeviceTopologyReader.ResolvePreferredDeviceName(
                "USB Input Device",
                @"USB\VID_1234&PID_5678",
                "Descriptor Product",
                "Catalog Product"));
        Assert.Equal(
            "Vendor Driver Name",
            WindowsDeviceTopologyReader.ResolvePreferredDeviceName(
                "Vendor Driver Name",
                @"USB\VID_1234&PID_5678",
                "Descriptor Product",
                "Catalog Product"));
    }

    [Theory]
    [InlineData(0u, DeviceNetworkConnectionStates.Unknown)]
    [InlineData(1u, DeviceNetworkConnectionStates.Connected)]
    [InlineData(2u, DeviceNetworkConnectionStates.Disconnected)]
    [InlineData(null, DeviceNetworkConnectionStates.NotReported)]
    public void DescribeNetworkConnectionState_MapsDocumentedValues(uint? state, string expected)
    {
        Assert.Equal(expected, WindowsDeviceTopologyReader.DescribeNetworkConnectionState(state));
    }

    [Fact]
    public void FormatNetworkLinkSpeed_HandlesSymmetricAsymmetricAndDisconnectedLinks()
    {
        Assert.Equal(
            "2.5 Gbps",
            WindowsDeviceTopologyReader.FormatNetworkLinkSpeed(NetworkAdapter(1, 2_500_000_000, 2_500_000_000)));
        // 收发不同时只报较高的那个，两者的明细由网络分区单独给出。
        Assert.Equal(
            "2.5 Gbps",
            WindowsDeviceTopologyReader.FormatNetworkLinkSpeed(NetworkAdapter(1, 1_000_000_000, 2_500_000_000)));
        Assert.Null(WindowsDeviceTopologyReader.FormatNetworkLinkSpeed(NetworkAdapter(2, 0, 0)));
    }

    [Fact]
    public void DisplayConfigNativeStructures_MatchWindowsAbi()
    {
        var sizes = WindowsDisplayPathTopologyReader.GetNativeStructureSizes();

        Assert.Equal(72, sizes.PathInfo);
        Assert.Equal(64, sizes.ModeInfo);
        Assert.Equal(420, sizes.TargetName);

        var capabilitySizes = WindowsDisplayPathTopologyReader.GetCapabilityStructureSizes();
        Assert.Equal(84, capabilitySizes.SourceName);
        Assert.Equal(32, capabilitySizes.AdvancedColor);
        Assert.Equal(24, capabilitySizes.SdrWhiteLevel);
        Assert.Equal(152, WindowsDxgiDisplayCapabilityReader.GetNativeDescriptionSize());
    }

    [Theory]
    [InlineData(0, "RGB / sRGB gamma / BT.709")]
    [InlineData(12, "RGB / PQ / BT.2020")]
    [InlineData(18, "YCbCr studio / HLG / BT.2020")]
    public void DxgiColorSpace_UsesDocumentedSemantics(int value, string expected)
    {
        Assert.Equal(expected, WindowsDxgiDisplayCapabilityReader.DescribeColorSpace(value));
    }

    [Fact]
    public void StorageAssociationReference_ParsesEscapedDeviceIdWithoutWmiRoundTrip()
    {
        var reference = @"\\HOST\root\cimv2:Win32_DiskDrive.DeviceID=""\\\\.\\PHYSICALDRIVE1""";

        Assert.Equal(
            @"\\.\PHYSICALDRIVE1",
            WindowsStorageDeviceCapabilityReader.ReadReferenceDeviceId(reference));
    }

    [Theory]
    [InlineData(@"USB\VID_0781&PID_55AE\DEVICE", true)]
    [InlineData(@"USBSTOR\DISK&VEN_SANDISK", true)]
    [InlineData(@"PCI\VEN_1022&DEV_15B6", false)]
    [InlineData(@"USB\ROOT_HUB30\ROOT", false)]
    [InlineData(@"SCSI\DISK&VEN_NVME", false)]
    public void RelatedStorageBinding_OnlyTargetsStorageTransportDevices(string deviceId, bool expected)
    {
        Assert.Equal(expected, WindowsStorageDeviceCapabilityReader.CanReceiveRelatedStorage(deviceId));
    }

    [Fact]
    public void OemDisplayConnectorCatalog_MatchesExactMechrevoMainboardOnly()
    {
        var matched = DeviceTopologyOemDisplayConnectorCatalog.Resolve(SystemIdentity(
            "MECHREVO",
            "JIAOLONG Series-X6DR55xx-B2"));
        var unknown = DeviceTopologyOemDisplayConnectorCatalog.Resolve(SystemIdentity(
            "MECHREVO",
            "OTHER-BOARD"));

        Assert.Collection(
            matched,
            profile =>
            {
                Assert.Equal(DeviceConnectorKinds.MiniDisplayPort, profile.ConnectorKind);
                Assert.Equal("Mini DisplayPort 2.1", profile.Protocol);
                Assert.Equal("UHBR20 / 80 Gbps", profile.PhysicalMaximumSpeed);
            },
            profile =>
            {
                Assert.Equal(DeviceConnectorKinds.Hdmi, profile.ConnectorKind);
                Assert.Equal("HDMI 2.1", profile.Protocol);
                Assert.Equal("48 Gbps", profile.PhysicalMaximumSpeed);
            });
        Assert.Empty(unknown);
    }

    [Theory]
    [InlineData(5, DeviceDisplayOutputTechnologies.Hdmi, DeviceConnectorKinds.Hdmi, true, false)]
    [InlineData(
        10,
        DeviceDisplayOutputTechnologies.DisplayPortExternal,
        DeviceConnectorKinds.DisplayPort,
        true,
        false)]
    [InlineData(
        18,
        DeviceDisplayOutputTechnologies.DisplayPortUsb4Tunnel,
        DeviceConnectorKinds.DisplayPort,
        true,
        false)]
    [InlineData(
        11,
        DeviceDisplayOutputTechnologies.DisplayPortEmbedded,
        DeviceConnectorKinds.InternalDisplay,
        false,
        true)]
    [InlineData(
        15,
        DeviceDisplayOutputTechnologies.Miracast,
        DeviceConnectorKinds.WirelessDisplay,
        false,
        false)]
    public void DisplayOutputTechnology_MapsDocumentedConnectorRoles(
        int technology,
        string expectedName,
        string expectedConnector,
        bool userConnectable,
        bool internalOutput)
    {
        Assert.Equal(expectedName, WindowsDisplayPathTopologyReader.DescribeOutputTechnology(technology));
        Assert.Equal(expectedConnector, WindowsDisplayPathTopologyReader.ResolveConnectorKind(technology));
        Assert.Equal(userConnectable, WindowsDisplayPathTopologyReader.IsUserConnectableOutput(technology));
        Assert.Equal(internalOutput, WindowsDisplayPathTopologyReader.IsInternalOutput(technology));
    }

    [Theory]
    [InlineData(60u, 1u, "60 Hz")]
    [InlineData(60_000u, 1_001u, "59.94 Hz")]
    [InlineData(0u, 0u, null)]
    public void FormatRefreshRate_HandlesExactAndFractionalRates(
        uint numerator,
        uint denominator,
        string? expected)
    {
        Assert.Equal(expected, WindowsDisplayPathTopologyReader.FormatRefreshRate(numerator, denominator));
    }

    [Fact]
    public void NormalizeMonitorDevicePath_ProducesPnPMonitorId()
    {
        Assert.Equal(
            @"DISPLAY\BOE0BCA\4&123456&0&UID0",
            WindowsDisplayPathTopologyReader.NormalizeMonitorDevicePath(
                @"\\?\DISPLAY#BOE0BCA#4&123456&0&UID0#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"));
    }

    [Fact]
    public void DuplicateDeviceIds_AreOrderIndependentAndConflictsAreNotPublished()
    {
        var first = DevicePort(@"USB\VID_0001", "First");
        var equivalent = first with
        {
            HardwareIds = [@"USB\CLASS_03", @"USB\VID_0001"],
            CompatibleIds = [@"USB\COMPATIBLE", @"USB\CLASS_03"]
        };
        first = first with
        {
            HardwareIds = [@"USB\VID_0001", @"USB\CLASS_03"],
            CompatibleIds = [@"USB\CLASS_03", @"USB\COMPATIBLE"]
        };
        var conflict = first with { DisplayName = "Conflicting" };

        var equivalentNotes = new List<BackendMessage>();
        var left = WindowsDeviceTopologyReader.ResolveDuplicateDeviceIds(
            [first, equivalent],
            equivalentNotes);
        var right = WindowsDeviceTopologyReader.ResolveDuplicateDeviceIds(
            [equivalent, first],
            []);

        Assert.Single(left);
        Assert.Single(right);
        Assert.Equal(
            DeviceTopologySemanticComparer.ComputeCanonicalPortPayload(left[0]),
            DeviceTopologySemanticComparer.ComputeCanonicalPortPayload(right[0]));
        Assert.Empty(equivalentNotes);

        var conflictNotes = new List<BackendMessage>();
        var conflicted = WindowsDeviceTopologyReader.ResolveDuplicateDeviceIds(
            [conflict, first],
            conflictNotes);

        Assert.Empty(conflicted);
        Assert.Single(conflictNotes);
        Assert.Equal(BackendMessageCodes.DeviceTopology.ConflictingFacts, conflictNotes[0].Code);
        Assert.Contains(@"USB\VID_0001", conflictNotes[0].Args[0], StringComparison.Ordinal);
    }

    private static DeviceTopologyUsbConnectorProperties Connector(bool userConnectable, bool typeC)
    {
        return new DeviceTopologyUsbConnectorProperties(
            userConnectable,
            PortIsDebugCapable: false,
            PortHasMultipleCompanions: false,
            PortConnectorIsTypeC: typeC,
            CompanionPorts: []);
    }

    private static DeviceTopologyPort DevicePort(string deviceId, string displayName)
    {
        return new DeviceTopologyPort(
            Id: $"device:{deviceId}",
            IsPhysicalConnector: false,
            DisplayName: displayName,
            ConnectorKind: DeviceConnectorKinds.Generic,
            BusKind: DeviceBusKinds.Usb,
            HardwareKind: "device",
            Protocol: "USB",
            Speed: "未知",
            DeviceId: deviceId,
            PnpClass: "HIDClass",
            Manufacturer: "Test",
            Service: "test",
            Status: "OK",
            Confidence: BackendMessage.Create(BackendMessageDomains.DeviceTopology, BackendMessageCodes.DeviceTopology.ConfidenceDeviceEnumeration),
            Source: BackendMessage.Create(BackendMessageDomains.DeviceTopology, BackendMessageCodes.DeviceTopology.SourcePnpEnumeration),
            UpstreamDeviceId: null,
            UpstreamDisplayName: null,
            TopologyPath: BackendMessage.Create(
                BackendMessageDomains.DeviceTopology,
                BackendMessageCodes.DeviceTopology.PathDeviceChain,
                deviceId),
            NativeParentDeviceId: null,
            NativeParentDisplayName: null,
            LocationInfo: null,
            LocationPaths: [],
            ClassGuid: null,
            Display: null,
            Network: null,
            IdResolution: null,
            AdvancedInterconnect: null,
            Usb: null,
            HardwareIds: [],
            CompatibleIds: []);
    }

    private static DeviceTopologySystemIdentity SystemIdentity(string manufacturer, string baseBoardProduct)
    {
        return new DeviceTopologySystemIdentity(
            manufacturer,
            "JIAOLONG Series",
            manufacturer,
            manufacturer,
            "test-bios",
            manufacturer,
            baseBoardProduct);
    }

    private static DeviceTopologyDisplayPath DisplayPath(
        uint targetId,
        int outputTechnology,
        string adapterDevicePath,
        bool active = false,
        bool available = false,
        uint connectorInstance = 0)
    {
        return new DeviceTopologyDisplayPath(
            AdapterHighPart: 0,
            AdapterLowPart: outputTechnology == 10 ? 2u : 1u,
            SourceId: 0,
            TargetId: targetId,
            OutputTechnology: outputTechnology,
            ConnectorInstance: connectorInstance,
            MonitorFriendlyName: null,
            MonitorDevicePath: null,
            Width: null,
            Height: null,
            RefreshRateNumerator: 0,
            RefreshRateDenominator: 0,
            Active: active,
            TargetAvailable: available,
            AdapterDevicePath: adapterDevicePath);
    }

    private static DeviceTopologyNetworkAdapter NetworkAdapter(uint state, ulong transmit, ulong receive)
    {
        return new DeviceTopologyNetworkAdapter(
            DeviceId: @"PCI\VEN_1234&DEV_5678",
            InterfaceName: "Ethernet",
            MediaConnectState: state,
            TransmitLinkSpeedBitsPerSecond: transmit,
            ReceiveLinkSpeedBitsPerSecond: receive,
            PermanentAddress: null,
            ActiveMtuBytes: 1500,
            HardwareInterface: true,
            ConnectorPresent: true);
    }
}
