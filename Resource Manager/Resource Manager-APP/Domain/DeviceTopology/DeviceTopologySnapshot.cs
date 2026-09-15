using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Domain.DeviceTopology;

public sealed record DeviceTopologySnapshot(
    DateTimeOffset CapturedAt,
    DeviceTopologySystemIdentity System,
    IReadOnlyList<DeviceTopologyPort> Ports,
    IReadOnlyList<BackendMessage> Notes);

public sealed record DeviceTopologySystemIdentity(
    string Manufacturer,
    string Model,
    string BrandDisplayName,
    string BrandLogoText,
    string? BiosVersion,
    string? BaseBoardManufacturer,
    string? BaseBoardProduct);

public sealed record DeviceTopologyPort(
    string Id,
    bool IsPhysicalConnector,
    string DisplayName,
    string ConnectorKind,
    string BusKind,
    string HardwareKind,
    string Protocol,
    string Speed,
    string DeviceId,
    string? PnpClass,
    string? Manufacturer,
    string? Service,
    string? Status,
    BackendMessage Confidence,
    BackendMessage Source,
    string? UpstreamDeviceId,
    string? UpstreamDisplayName,
    string TopologyPath,
    string? NativeParentDeviceId,
    string? NativeParentDisplayName,
    string? LocationInfo,
    IReadOnlyList<string> LocationPaths,
    string? ClassGuid,
    DeviceTopologyDisplayConnection? Display,
    DeviceTopologyNetworkConnection? Network,
    DeviceTopologyIdResolution? IdResolution,
    DeviceTopologyAdvancedInterconnect? AdvancedInterconnect,
    DeviceTopologyUsbConnection? Usb,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    uint? DevNodeStatus = null,
    uint? ProblemCode = null,
    string? PhysicalMaximumSpeed = null,
    DeviceTopologyHidCapabilities? Hid = null,
    DeviceTopologyCameraCapabilities? Camera = null,
    DeviceTopologySmartDeviceCapabilities? SmartDevice = null,
    DeviceTopologyStorageDevice? Storage = null);

public sealed record DeviceTopologyDisplayConnection(
    string ConnectorTechnology,
    string MonitorName,
    string? Resolution,
    string RefreshRate,
    bool Active,
    bool TargetAvailable,
    bool Internal,
    uint ConnectorInstance,
    string? MonitorDevicePath,
    uint? BitsPerColorChannel = null,
    string? ColorEncoding = null,
    bool? AdvancedColorSupported = null,
    bool? AdvancedColorEnabled = null,
    bool? WideColorEnforced = null,
    double? SdrWhiteLevelNits = null,
    string? HdrFormats = null,
    string? DisplayTechnology = null,
    string? PanelTechnology = null,
    string? EdidVersion = null,
    string? EdidProductName = null,
    string? EdidSerialNumber = null,
    string? PhysicalSize = null,
    double? MinimumLuminanceNits = null,
    double? MaximumLuminanceNits = null,
    double? MaximumFullFrameLuminanceNits = null,
    string? ColorCapabilitySource = null,
    string? ColorSpace = null);

public sealed record DeviceTopologyNetworkConnection(
    string? InterfaceName,
    string ConnectionState,
    string? TransmitLinkSpeed,
    string? ReceiveLinkSpeed,
    string? PermanentAddress,
    ulong? ActiveMtuBytes,
    bool? HardwareInterface,
    bool? ConnectorPresent);

public sealed record DeviceTopologyIdResolution(
    string Database,
    string Version,
    string? VendorName,
    string? DeviceName,
    string? SubsystemName);

public sealed record DeviceTopologyAdvancedInterconnect(
    string Kind,
    BackendMessage Role,
    string Technology,
    BackendMessage Evidence);

public sealed record DeviceTopologyUsbConnection(
    string HubDevicePath,
    uint PortNumber,
    bool DeviceConnected,
    string ConnectionStatus,
    string NegotiatedSpeed,
    ushort DeviceAddress,
    string? VendorId,
    string? ProductId,
    bool DeviceIsHub,
    string SupportedProtocols,
    bool? OperatingAtSuperSpeedOrHigher,
    bool? SuperSpeedCapableOrHigher,
    bool? OperatingAtSuperSpeedPlusOrHigher,
    bool? SuperSpeedPlusCapableOrHigher,
    bool? PortIsUserConnectable,
    bool? PortIsDebugCapable,
    bool? PortHasMultipleCompanions,
    bool? PortConnectorIsTypeC,
    IReadOnlyList<DeviceTopologyUsbCompanionPort> CompanionPorts,
    string DeviceSpecification,
    string DeviceRevision,
    string DeviceClass,
    string? ManufacturerName,
    string? ProductName,
    string? SerialNumber,
    IReadOnlyList<string> InterfaceProtocols,
    string? DownstreamHubDevicePath = null,
    IReadOnlyList<DeviceTopologyUsbEndpoint>? Endpoints = null);

public sealed record DeviceTopologyUsbEndpoint(
    byte InterfaceNumber,
    byte AlternateSetting,
    string InterfaceProtocol,
    byte EndpointAddress,
    string Direction,
    string TransferType,
    ushort MaximumPacketSize,
    byte Interval,
    double? ServiceIntervalMicroseconds,
    double? TheoreticalReportRateHz);

public sealed record DeviceTopologyHidCapabilities(
    string HidType,
    string? HidSpecification,
    double? InputPollingIntervalMicroseconds,
    double? TheoreticalReportRateHz,
    uint? ReportedDpi,
    uint? ReportedScanRateHz,
    string StandardCapabilitySource,
    string? VendorCapabilitySource);

public sealed record DeviceTopologyCameraCapabilities(
    string CapabilitySource,
    DeviceTopologyCameraMode? BestMode,
    IReadOnlyList<DeviceTopologyCameraMode> NativeModes);

public sealed record DeviceTopologyCameraMode(
    uint Width,
    uint Height,
    double MaximumFrameRate,
    string PixelFormat);

public sealed record DeviceTopologySmartDeviceCapabilities(
    string DeviceType,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    string? Protocol,
    string? Transport,
    uint? BatteryPercent,
    IReadOnlyList<DeviceTopologySmartDeviceStorage> Storages,
    string Source);

public sealed record DeviceTopologySmartDeviceStorage(
    string Name,
    ulong? CapacityBytes,
    ulong? FreeBytes,
    string? FileSystem);

public sealed record DeviceTopologyStorageDevice(
    string PhysicalDeviceId,
    string? Model,
    string? Manufacturer,
    string? SerialNumber,
    string? FirmwareRevision,
    string? MediaType,
    string? BusType,
    ulong? CapacityBytes,
    uint? BytesPerSector,
    string? PartitionStyle,
    string? HealthStatus,
    IReadOnlyList<DeviceTopologyStoragePartition> Partitions,
    string Source);

public sealed record DeviceTopologyStoragePartition(
    string DeviceId,
    uint? PartitionNumber,
    string? Type,
    ulong? CapacityBytes,
    ulong? StartingOffsetBytes,
    bool? Bootable,
    bool? BootPartition,
    bool? PrimaryPartition,
    IReadOnlyList<DeviceTopologyStorageVolume> Volumes);

public sealed record DeviceTopologyStorageVolume(
    string? DriveLetter,
    string? Label,
    string? FileSystem,
    ulong? CapacityBytes,
    ulong? FreeBytes,
    string MountState,
    string? VolumeSerialNumber);

public sealed record DeviceTopologyUsbCompanionPort(
    ushort CompanionIndex,
    ushort PortNumber,
    string? HubSymbolicLinkName);

public static class DeviceConnectorKinds
{
    public const string UsbA = "usb-a";
    public const string UsbC = "usb-c";
    public const string Thunderbolt = "thunderbolt";
    public const string Hdmi = "hdmi";
    public const string DisplayPort = "displayport";
    public const string MiniDisplayPort = "mini-displayport";
    public const string Dvi = "dvi";
    public const string Vga = "vga";
    public const string InternalDisplay = "internal-display";
    public const string WirelessDisplay = "wireless-display";
    public const string Rj45 = "rj45";
    public const string Audio = "audio";
    public const string Power = "power";
    public const string SdCard = "sd-card";
    public const string Pcie = "pcie";
    public const string Bluetooth = "bluetooth";
    public const string Generic = "generic";
}

public static class DeviceBusKinds
{
    public const string Usb = "usb";
    public const string Usb4 = "usb4";
    public const string Thunderbolt = "thunderbolt";
    public const string Pci = "pci";
    public const string Display = "display";
    public const string Network = "network";
    public const string Audio = "audio";
    public const string Storage = "storage";
    public const string Bluetooth = "bluetooth";
    public const string System = "system";
    public const string Unknown = "unknown";
}

public static class DeviceInterconnectKinds
{
    public const string UcsiConnectorManager = "ucsi-connector-manager";
    public const string Usb4HostRouter = "usb4-host-router";
    public const string Usb4DeviceRouter = "usb4-device-router";
    public const string Usb4P2PNetwork = "usb4-p2p-network";
}
