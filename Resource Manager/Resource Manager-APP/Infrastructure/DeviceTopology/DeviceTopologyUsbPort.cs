namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal sealed record DeviceTopologyUsbPortSnapshot(
    IReadOnlyList<DeviceTopologyUsbPort> Ports,
    IReadOnlyDictionary<string, DeviceTopologyUsbPort> PortsByDriverKey,
    string? Error);

internal sealed record DeviceTopologyUsbPort(
    string HubDevicePath,
    uint PortNumber,
    bool DeviceConnected,
    string ConnectionStatus,
    string NegotiatedSpeed,
    ushort VendorId,
    ushort ProductId,
    ushort DeviceAddress,
    bool DeviceIsHub,
    string? DriverKeyName,
    DeviceTopologyUsbConnectorProperties? ConnectorProperties,
    DeviceTopologyUsbPortCapability? Capability,
    DeviceTopologyUsbDescriptor Descriptor,
    string? DownstreamHubDevicePath = null,
    byte? NegotiatedSpeedCode = null);

internal sealed record DeviceTopologyUsbConnectorProperties(
    bool PortIsUserConnectable,
    bool PortIsDebugCapable,
    bool PortHasMultipleCompanions,
    bool PortConnectorIsTypeC,
    IReadOnlyList<DeviceTopologyUsbCompanionPortInfo> CompanionPorts);

internal sealed record DeviceTopologyUsbCompanionPortInfo(
    ushort CompanionIndex,
    ushort PortNumber,
    string? HubSymbolicLinkName);

internal sealed record DeviceTopologyUsbConnectorPropertiesEntry(
    uint PropertyFlags,
    ushort CompanionIndex,
    ushort CompanionPortNumber,
    string? CompanionHubSymbolicLinkName);

internal sealed record DeviceTopologyUsbPortCapability(
    bool SupportsUsb11,
    bool SupportsUsb20,
    bool SupportsUsb30,
    bool OperatingAtSuperSpeedOrHigher,
    bool SuperSpeedCapableOrHigher,
    bool OperatingAtSuperSpeedPlusOrHigher,
    bool SuperSpeedPlusCapableOrHigher);

internal sealed record DeviceTopologyUsbDescriptorHeader(
    ushort UsbVersionBcd,
    ushort DeviceRevisionBcd,
    byte DeviceClass,
    byte DeviceSubClass,
    byte DeviceProtocol,
    byte ManufacturerIndex,
    byte ProductIndex,
    byte SerialNumberIndex,
    byte ConfigurationCount,
    byte CurrentConfigurationValue);

internal sealed record DeviceTopologyUsbDescriptor(
    string DeviceSpecification,
    string DeviceRevision,
    string DeviceClass,
    string? ManufacturerName,
    string? ProductName,
    string? SerialNumber,
    IReadOnlyList<string> InterfaceProtocols,
    IReadOnlyList<DeviceTopologyUsbEndpointDescriptor> Endpoints,
    IReadOnlyList<DeviceTopologyUsbHidDescriptor> HidDescriptors,
    IReadOnlyList<DeviceTopologyUsbCameraModeDescriptor> CameraModes);

internal sealed record DeviceTopologyUsbEndpointDescriptor(
    byte InterfaceNumber,
    byte AlternateSetting,
    byte InterfaceClass,
    byte InterfaceSubClass,
    byte InterfaceProtocol,
    byte EndpointAddress,
    byte TransferType,
    ushort MaximumPacketSize,
    byte Interval);

internal sealed record DeviceTopologyUsbHidDescriptor(
    byte InterfaceNumber,
    byte AlternateSetting,
    byte InterfaceSubClass,
    byte InterfaceProtocol,
    ushort HidVersionBcd,
    byte CountryCode,
    ushort? ReportDescriptorLength);

internal sealed record DeviceTopologyUsbCameraModeDescriptor(
    uint Width,
    uint Height,
    double MaximumFrameRate,
    string PixelFormat);
