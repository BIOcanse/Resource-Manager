import type {
  DeviceTopologyCameraMode,
  DeviceTopologyPort,
  DeviceTopologyStoragePartition,
  DeviceTopologySmartDeviceStorage
} from "../../types";

export type DeviceAdapterKind =
  | "monitor"
  | "external-gpu-dock"
  | "dock"
  | "usb-hub"
  | "external-storage"
  | "keyboard"
  | "mouse"
  | "audio-device"
  | "camera"
  | "mobile-device"
  | "power-input"
  | "graphics-adapter"
  | "network-adapter"
  | "bluetooth-device"
  | "internal-controller"
  | "generic-device";

export type DeviceAdapterIconKind =
  | "monitor"
  | "external-gpu"
  | "dock"
  | "usb-hub"
  | "hard-drive"
  | "keyboard"
  | "mouse"
  | "headphones"
  | "camera"
  | "smartphone"
  | "tablet"
  | "laptop"
  | "power-input"
  | "graphics-card"
  | "network-adapter"
  | "bluetooth-device"
  | "controller"
  | "device";

export interface SpecializedDeviceSummaryField {
  label: string;
  value: string;
}

export interface SpecializedInternalDeviceFacts {
  transport: string;
  manufacturer: string;
  driverService: string;
  deviceStatus: string;
  location: string;
  catalogIdentity: string;
}

export interface DeviceAdapterContext {
  scope: "external" | "internal";
  port: DeviceTopologyPort;
  fallbackTitle: string;
  parentTitle?: string;
  downstreamInterfaceCount: number;
  connectedDownstreamInterfaceCount: number;
}

interface SpecializedDeviceBase {
  adapterId: DeviceAdapterKind;
  kind: DeviceAdapterKind;
  deviceTypeLabel: string;
  title: string;
  subtitle: string;
  badge: string;
  iconKind: DeviceAdapterIconKind;
  upstreamInterface: string;
  currentLink: string;
  summaryFields: SpecializedDeviceSummaryField[];
  capabilityLabels: string[];
  searchTerms: string[];
  internalFacts?: SpecializedInternalDeviceFacts;
}

export interface MonitorDeviceModel extends SpecializedDeviceBase {
  kind: "monitor";
  adapterId: "monitor";
  resolution: string;
  refreshRate: string;
  connectorTechnology: string;
  displayState: string;
  hdrState: string;
  bitDepth: string;
  colorEncoding: string;
  colorSpace: string;
  displayTechnology: string;
  panelTechnology: string;
  sdrWhiteLevel: string;
  peakLuminance: string;
  fullFrameLuminance: string;
  physicalSize: string;
}

export interface ExternalGpuDockDeviceModel extends SpecializedDeviceBase {
  kind: "external-gpu-dock";
  adapterId: "external-gpu-dock";
  interconnectTechnology: string;
  gpuIdentity: string;
  relationEvidence: string;
}

export interface DockDeviceModel extends SpecializedDeviceBase {
  kind: "dock";
  adapterId: "dock";
  downstreamInterfaceCount: number;
  connectedDownstreamInterfaceCount: number;
  idleDownstreamInterfaceCount: number;
  usbSpecification: string;
}

export interface UsbHubDeviceModel extends SpecializedDeviceBase {
  kind: "usb-hub";
  adapterId: "usb-hub";
  downstreamInterfaceCount: number;
  connectedDownstreamInterfaceCount: number;
  idleDownstreamInterfaceCount: number;
  usbSpecification: string;
}

export interface ExternalStorageDeviceModel extends SpecializedDeviceBase {
  kind: "external-storage";
  adapterId: "external-storage";
  transportMode: string;
  usbSpecification: string;
  deviceRevision: string;
  interfaceProtocols: string[];
  capacity: string;
  busType: string;
  mediaType: string;
  partitionStyle: string;
  healthStatus: string;
  partitions: DeviceTopologyStoragePartition[];
}

export interface KeyboardDeviceModel extends SpecializedDeviceBase {
  kind: "keyboard";
  adapterId: "keyboard";
  hidMode: string;
  deviceRevision: string;
  interfaceProtocols: string[];
  pollingInterval: string;
  reportRate: string;
  scanRate: string;
}

export interface MouseDeviceModel extends SpecializedDeviceBase {
  kind: "mouse";
  adapterId: "mouse";
  hidMode: string;
  deviceRevision: string;
  interfaceProtocols: string[];
  pollingInterval: string;
  reportRate: string;
  dpi: string;
}

export interface AudioDeviceModel extends SpecializedDeviceBase {
  kind: "audio-device";
  adapterId: "audio-device";
  audioControl: boolean;
  audioStreaming: boolean;
  hidControls: boolean;
  interfaceProtocols: string[];
}

export interface CameraDeviceModel extends SpecializedDeviceBase {
  kind: "camera";
  adapterId: "camera";
  videoControl: boolean;
  videoStreaming: boolean;
  audioCapable: boolean;
  interfaceProtocols: string[];
  bestMode: string;
  nativeModes: DeviceTopologyCameraMode[];
  capabilitySource: string;
}

export interface MobileDeviceModel extends SpecializedDeviceBase {
  kind: "mobile-device";
  adapterId: "mobile-device";
  mediaTransfer: boolean;
  dataConnection: boolean;
  vendorChannel: boolean;
  interfaceProtocols: string[];
  manufacturer: string;
  model: string;
  firmwareVersion: string;
  protocol: string;
  transport: string;
  battery: string;
  storages: DeviceTopologySmartDeviceStorage[];
}

export interface PowerInputDeviceModel extends SpecializedDeviceBase {
  kind: "power-input";
  adapterId: "power-input";
  inputRole: string;
  negotiatedPower: string;
  relationEvidence: string;
}

export interface GraphicsAdapterDeviceModel extends SpecializedDeviceBase {
  kind: "graphics-adapter";
  adapterId: "graphics-adapter";
  manufacturer: string;
  driverService: string;
  deviceStatus: string;
  busType: string;
}

export interface NetworkAdapterDeviceModel extends SpecializedDeviceBase {
  kind: "network-adapter";
  adapterId: "network-adapter";
  interfaceName: string;
  connectionState: string;
  transmitSpeed: string;
  receiveSpeed: string;
  activeMtu: string;
  permanentAddress: string;
}

export interface BluetoothDeviceModel extends SpecializedDeviceBase {
  kind: "bluetooth-device";
  adapterId: "bluetooth-device";
  bluetoothRole: string;
  protocol: string;
  driverService: string;
  deviceStatus: string;
}

export interface InternalControllerDeviceModel extends SpecializedDeviceBase {
  kind: "internal-controller";
  adapterId: "internal-controller";
  controllerType: string;
  interconnectTechnology: string;
  driverService: string;
  deviceStatus: string;
  downstreamDeviceCount: number;
}

export interface GenericDeviceModel extends SpecializedDeviceBase {
  kind: "generic-device";
  adapterId: "generic-device";
  deviceClass: string;
  usbSpecification: string;
  deviceRevision: string;
  interfaceProtocols: string[];
}

export type SpecializedDeviceModel =
  | MonitorDeviceModel
  | ExternalGpuDockDeviceModel
  | DockDeviceModel
  | UsbHubDeviceModel
  | ExternalStorageDeviceModel
  | KeyboardDeviceModel
  | MouseDeviceModel
  | AudioDeviceModel
  | CameraDeviceModel
  | MobileDeviceModel
  | PowerInputDeviceModel
  | GraphicsAdapterDeviceModel
  | NetworkAdapterDeviceModel
  | BluetoothDeviceModel
  | InternalControllerDeviceModel
  | GenericDeviceModel;

export interface DeviceAdapter<TModel extends SpecializedDeviceModel = SpecializedDeviceModel> {
  id: DeviceAdapterKind;
  matches(context: DeviceAdapterContext): boolean;
  createModel(context: DeviceAdapterContext): TModel;
}
