
using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Windows.Devices.Usb;

public sealed record DeviceTopologyUsbPortSnapshot(
    IReadOnlyList<DeviceTopologyUsbPort> Ports,
    IReadOnlyDictionary<string, DeviceTopologyUsbPort> PortsByDriverKey,
    string? Error);

public sealed record DeviceTopologyUsbPort(
    string HubDevicePath,
    uint PortNumber,
    bool DeviceConnected,
    uint ConnectionStatus,
    string? NegotiatedSpeed,
    ushort VendorId,
    ushort ProductId,
    ushort DeviceAddress,
    bool DeviceIsHub,
    string? DriverKeyName,
    DeviceTopologyUsbConnectorProperties? ConnectorProperties,
    DeviceTopologyUsbPortCapability? Capability,
    DeviceTopologyUsbDescriptor Descriptor,
    string? DownstreamHubDevicePath = null,
    byte? NegotiatedSpeedCode = null,
    double? NegotiatedLinkSpeedMegabitsPerSecond = null)
{
    public double? DeclaredMaximumLinkSpeedMegabitsPerSecond
        => Descriptor.DeclaredMaximumLinkSpeedMegabitsPerSecond;
}

public sealed record DeviceTopologyUsbConnectorProperties(
    bool PortIsUserConnectable,
    bool PortIsDebugCapable,
    bool PortHasMultipleCompanions,
    bool PortConnectorIsTypeC,
    IReadOnlyList<DeviceTopologyUsbCompanionPortInfo> CompanionPorts);

public sealed record DeviceTopologyUsbCompanionPortInfo(
    ushort CompanionIndex,
    ushort PortNumber,
    string? HubSymbolicLinkName);

public sealed record DeviceTopologyUsbConnectorPropertiesEntry(
    uint PropertyFlags,
    ushort CompanionIndex,
    ushort CompanionPortNumber,
    string? CompanionHubSymbolicLinkName);

public sealed record DeviceTopologyUsbPortCapability(
    bool SupportsUsb11,
    bool SupportsUsb20,
    bool SupportsUsb30,
    bool OperatingAtSuperSpeedOrHigher,
    bool SuperSpeedCapableOrHigher,
    bool OperatingAtSuperSpeedPlusOrHigher,
    bool SuperSpeedPlusCapableOrHigher);

public sealed record DeviceTopologyUsbDescriptorHeader(
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

public sealed record DeviceTopologyUsbDescriptor(
    string DeviceSpecification,
    string DeviceRevision,
    string DeviceClass,
    string? ManufacturerName,
    string? ProductName,
    string? SerialNumber,
    IReadOnlyList<string> InterfaceProtocols,
    IReadOnlyList<DeviceTopologyUsbEndpointDescriptor> Endpoints,
    IReadOnlyList<DeviceTopologyUsbHidDescriptor> HidDescriptors,
    IReadOnlyList<DeviceTopologyUsbCameraModeDescriptor> CameraModes,
    double? DeclaredMaximumLinkSpeedMegabitsPerSecond = null,
    DeviceTopologyUsbSuperSpeedPlusCapability? SuperSpeedPlusCapability = null,
    byte? DeviceClassCode = null,
    byte? DeviceSubClassCode = null,
    byte? DeviceProtocolCode = null,
    IReadOnlyList<DeviceTopologyUsbInterfaceDescriptor>? Interfaces = null);

/// <summary>Raw interface descriptor; includes alternate settings and interfaces without endpoints.</summary>
public sealed record DeviceTopologyUsbInterfaceDescriptor(
    byte InterfaceNumber, byte AlternateSetting, byte ClassCode, byte SubClassCode, byte ProtocolCode);

/// <summary>
/// Raw device-declared SuperSpeedPlus BOS capability. Sublink attributes describe
/// per-sublink maximum lane rates; this is not an aggregate device/link maximum.
/// </summary>
public sealed record DeviceTopologyUsbSuperSpeedPlusCapability(
    byte DescriptorLength,
    byte DescriptorType,
    byte DeviceCapabilityType,
    byte ReservedByte,
    uint RawBmAttributes,
    ushort RawFunctionalitySupport,
    ushort RawReservedWord,
    ImmutableArray<DeviceTopologyUsbSublinkSpeedAttribute> SublinkSpeedAttributes)
{
    public int SublinkSpeedAttributeCount => (int)(RawBmAttributes & 0x1F) + 1;
    public int SublinkSpeedIdCount => (int)((RawBmAttributes >> 5) & 0x0F) + 1;
    public byte MinimumSublinkSpeedAttributeId => (byte)(RawFunctionalitySupport & 0x0F);

    // Retain the descriptor's encoded nibbles. They are minimum-functionality
    // declarations; this API does not convert them into maximum lane counts.
    public byte MinimumReceiveLaneCountEncoded => (byte)((RawFunctionalitySupport >> 8) & 0x0F);
    public byte MinimumTransmitLaneCountEncoded => (byte)((RawFunctionalitySupport >> 12) & 0x0F);
    public byte FunctionalityReservedBits => (byte)((RawFunctionalitySupport >> 4) & 0x0F);
    public uint AttributesReservedBits => RawBmAttributes & 0xFFFFFE00u;
}

/// <summary>A single raw 32-bit BOS sublink-speed attribute and its decoded fields.</summary>
public sealed record DeviceTopologyUsbSublinkSpeedAttribute(uint RawValue)
{
    public byte SublinkSpeedAttributeId => (byte)(RawValue & 0x0F);
    public byte LaneSpeedExponent => (byte)((RawValue >> 4) & 0x03);
    /// <summary>0 is symmetric; 1 is asymmetric. The raw value remains available.</summary>
    public byte SublinkTypeMode => (byte)((RawValue >> 6) & 0x01);
    /// <summary>0 is receive; 1 is transmit. The raw value remains available.</summary>
    public byte SublinkTypeDirection => (byte)((RawValue >> 7) & 0x01);
    public byte ReservedBits => (byte)((RawValue >> 8) & 0x3F);
    public byte LinkProtocol => (byte)((RawValue >> 14) & 0x03);
    public ushort LaneSpeedMantissa => (ushort)(RawValue >> 16);

    /// <summary>Per-sublink maximum rate represented by LSM and LSE, in bits/s.</summary>
    public decimal MaximumLaneBitRatePerSecond => LaneSpeedMantissa * (LaneSpeedExponent switch
    {
        0 => 1m,
        1 => 1_000m,
        2 => 1_000_000m,
        3 => 1_000_000_000m,
        _ => throw new InvalidOperationException("Lane-speed exponent is outside its two-bit range.")
    });

    public decimal MaximumLaneMegabitsPerSecond => MaximumLaneBitRatePerSecond / 1_000_000m;
}

public sealed record DeviceTopologyUsbEndpointDescriptor(
    byte InterfaceNumber,
    byte AlternateSetting,
    byte InterfaceClass,
    byte InterfaceSubClass,
    byte InterfaceProtocol,
    byte EndpointAddress,
    byte TransferType,
    ushort MaximumPacketSize,
    byte Interval);

public sealed record DeviceTopologyUsbHidDescriptor(
    byte InterfaceNumber,
    byte AlternateSetting,
    byte InterfaceSubClass,
    byte InterfaceProtocol,
    ushort HidVersionBcd,
    byte CountryCode,
    ushort? ReportDescriptorLength);

public sealed record DeviceTopologyUsbCameraModeDescriptor(
    uint Width,
    uint Height,
    double MaximumFrameRate,
    string PixelFormat)
{
    /// <summary>Original integer descriptor evidence, absent on legacy caller-created modes.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DeviceTopologyUsbCameraFrameIntervals? FrameIntervalsDeclared { get; init; }
}
