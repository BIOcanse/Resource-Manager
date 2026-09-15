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
    /// <summary>设备自报的名字；我们自己生成名字时为 null，改由 <see cref="DisplayNameCode"/> 给出。</summary>
    string? DisplayName,
    string ConnectorKind,
    string BusKind,
    /// <summary>硬件类别的技术名（USB Hub、Ethernet 一类）；描述性名字走 <see cref="HardwareKindCode"/>。</summary>
    string? HardwareKind,
    string Protocol,
    string? Speed,
    string DeviceId,
    string? PnpClass,
    string? Manufacturer,
    string? Service,
    string? Status,
    BackendMessage Confidence,
    BackendMessage Source,
    string? UpstreamDeviceId,
    string? UpstreamDisplayName,
    BackendMessage TopologyPath,
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
    DeviceTopologyStorageDevice? Storage = null,
    BackendMessage? DisplayNameCode = null,
    BackendMessage? HardwareKindCode = null);

public sealed record DeviceTopologyDisplayConnection(
    /// <summary>机型接口档案给的接口名（「HDMI 2.1」一类）；没有档案时为 null。</summary>
    string? ConnectorTechnology,
    /// <summary>Windows 输出技术，取值见 <see cref="DeviceDisplayOutputTechnologies"/>。</summary>
    string OutputTechnology,
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
    BackendMessage? DisplayTechnology = null,
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
    /// <summary>取值见 <see cref="DeviceNetworkConnectionStates"/>，措辞由前端出。</summary>
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
    BackendMessage ConnectionStatus,
    string? NegotiatedSpeed,
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
    /// <summary>取值见 <see cref="DeviceEndpointDirections"/>。</summary>
    string Direction,
    /// <summary>取值见 <see cref="DeviceEndpointTransferTypes"/>。</summary>
    string TransferType,
    ushort MaximumPacketSize,
    byte Interval,
    double? ServiceIntervalMicroseconds,
    double? TheoreticalReportRateHz);

public sealed record DeviceTopologyHidCapabilities(
    /// <summary>取值见 <see cref="DeviceHidTypes"/>。</summary>
    string HidType,
    string? HidSpecification,
    double? InputPollingIntervalMicroseconds,
    double? TheoreticalReportRateHz,
    uint? ReportedDpi,
    uint? ReportedScanRateHz,
    BackendMessage StandardCapabilitySource,
    BackendMessage? VendorCapabilitySource);

public sealed record DeviceTopologyCameraCapabilities(
    BackendMessage CapabilitySource,
    DeviceTopologyCameraMode? BestMode,
    IReadOnlyList<DeviceTopologyCameraMode> NativeModes);

public sealed record DeviceTopologyCameraMode(
    uint Width,
    uint Height,
    double MaximumFrameRate,
    string PixelFormat);

public sealed record DeviceTopologySmartDeviceCapabilities(
    /// <summary>取值见 <see cref="DevicePortableDeviceTypes"/>。</summary>
    string DeviceType,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? FirmwareVersion,
    string? Protocol,
    string? Transport,
    uint? BatteryPercent,
    IReadOnlyList<DeviceTopologySmartDeviceStorage> Storages,
    BackendMessage Source);

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
    /// <summary>取值见 <see cref="DeviceStorageHealthStates"/>。</summary>
    string? HealthStatus,
    IReadOnlyList<DeviceTopologyStoragePartition> Partitions,
    BackendMessage Source);

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
    /// <summary>取值见 <see cref="DeviceStorageMountStates"/>。</summary>
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

/// <summary>
/// Windows DISPLAYCONFIG_OUTPUT_TECHNOLOGY 的取值。措辞在前端，这里只有标识。
/// </summary>
public static class DeviceDisplayOutputTechnologies
{
    public const string Other = "other";
    public const string Vga = "vga";
    public const string SVideo = "s-video";
    public const string CompositeVideo = "composite-video";
    public const string ComponentVideo = "component-video";
    public const string Dvi = "dvi";
    public const string Hdmi = "hdmi";
    public const string Lvds = "lvds";
    public const string DJpn = "d-jpn";
    public const string Sdi = "sdi";
    public const string DisplayPortExternal = "displayport-external";
    public const string DisplayPortEmbedded = "displayport-embedded";
    public const string UdiExternal = "udi-external";
    public const string UdiEmbedded = "udi-embedded";
    public const string SdtvDongle = "sdtv-dongle";
    public const string Miracast = "miracast";
    public const string IndirectWired = "indirect-wired";
    public const string IndirectVirtual = "indirect-virtual";
    public const string DisplayPortUsb4Tunnel = "displayport-usb4-tunnel";
    public const string Internal = "internal";
    public const string Unknown = "unknown";
}

/// <summary>EDID 视频输入定义里的数字接口取值。</summary>
public static class DeviceEdidDigitalInterfaces
{
    public const string Analog = "analog";
    public const string Undefined = "undefined";
    public const string Dvi = "dvi";
    public const string HdmiTypeA = "hdmi-type-a";
    public const string HdmiTypeB = "hdmi-type-b";
    public const string Mddi = "mddi";
    public const string DisplayPort = "displayport";
    public const string Reserved = "reserved";
}

/// <summary>HID 设备的类别。只有类别，没有措辞。</summary>
public static class DeviceHidTypes
{
    public const string Keyboard = "keyboard";
    public const string Mouse = "mouse";
    public const string InputDevice = "input-device";
}

/// <summary>USB 端点方向。</summary>
public static class DeviceEndpointDirections
{
    public const string Input = "input";
    public const string Output = "output";
}

/// <summary>USB 端点传输类型。</summary>
public static class DeviceEndpointTransferTypes
{
    public const string Control = "control";
    public const string Isochronous = "isochronous";
    public const string Bulk = "bulk";
    public const string Interrupt = "interrupt";
    public const string Unknown = "unknown";
}

/// <summary>便携智能设备的类别。</summary>
public static class DevicePortableDeviceTypes
{
    public const string Phone = "phone";
    public const string Tablet = "tablet";
    public const string Camera = "camera";
    public const string SmartDevice = "smart-device";
}

/// <summary>卷的挂载状态。</summary>
public static class DeviceStorageMountStates
{
    public const string Mounted = "mounted";
    public const string NotReady = "not-ready";
    public const string Unknown = "unknown";
}

/// <summary>磁盘的健康状态。</summary>
public static class DeviceStorageHealthStates
{
    public const string Healthy = "healthy";
    public const string Warning = "warning";
    public const string Unhealthy = "unhealthy";
    public const string Unknown = "unknown";
}

/// <summary>网络接口的连接状态。只有状态，没有措辞。</summary>
public static class DeviceNetworkConnectionStates
{
    public const string Connected = "connected";
    public const string Disconnected = "disconnected";
    public const string Unknown = "unknown";
    public const string NotReported = "not-reported";
}

public static class DeviceInterconnectKinds
{
    public const string UcsiConnectorManager = "ucsi-connector-manager";
    public const string Usb4HostRouter = "usb4-host-router";
    public const string Usb4DeviceRouter = "usb4-device-router";
    public const string Usb4P2PNetwork = "usb4-p2p-network";
}
