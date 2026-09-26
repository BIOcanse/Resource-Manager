import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  buildInternalInterfaceTree,
  filterInternalInterfaceTree
} from "../src/features/deviceTopology/internalInterfaceTree.ts";
import type { DeviceTopologyPort, DeviceTopologyUsbConnection } from "../src/types.ts";

const externalDeviceId = String.raw`USB\VID_1111&PID_2222\EXTERNAL`;
const externalPort = port({
  id: "external-port",
  name: "External Keyboard",
  deviceId: externalDeviceId,
  busKind: "usb",
  hardwareKind: "USB Device",
  connectorKind: "usb-a",
  isPhysicalConnector: true,
  pnpClass: "HIDClass",
  usb: usbConnection("external", true),
  hid: {
    hidType: "HID 键盘",
    inputPollingIntervalMicroseconds: 1_000,
    theoreticalReportRateHz: 1_000,
    standardCapabilitySource: "test"
  }
});
const externalFunction = port({
  id: "external-function",
  name: "External Keyboard Function",
  deviceId: String.raw`HID\VID_1111&PID_2222&MI_00\FUNCTION`,
  nativeParentDeviceId: externalDeviceId,
  hardwareKind: "Keyboard",
  pnpClass: "Keyboard",
  service: "kbdhid"
});

const usbControllerId = String.raw`PCI\VEN_1022&DEV_USB\CONTROLLER`;
const rootHubId = String.raw`USB\ROOT_HUB30\INTERNAL`;
const cameraId = String.raw`USB\VID_3333&PID_4444\INTEGRATED_CAMERA`;
const usbController = port({
  id: "usb-controller",
  name: "AMD USB Host Controller",
  deviceId: usbControllerId,
  nativeParentDeviceId: String.raw`PCI\ROOT_PORT\USB`,
  busKind: "usb",
  hardwareKind: "USB Host Controller",
  pnpClass: "USB",
  service: "USBXHCI"
});
const rootHub = port({
  id: "root-hub",
  name: "USB Root Hub",
  deviceId: rootHubId,
  nativeParentDeviceId: usbControllerId,
  busKind: "usb",
  hardwareKind: "USB Hub",
  pnpClass: "USB",
  service: "USBHUB3"
});
const internalCamera = port({
  id: "internal-camera",
  name: "Integrated Camera",
  deviceId: cameraId,
  nativeParentDeviceId: rootHubId,
  busKind: "usb",
  hardwareKind: "USB Composite Device",
  pnpClass: "USB",
  service: "usbccgp",
  usb: {
    ...usbConnection("internal", false),
    productName: "USB Composite Device"
  },
  camera: {
    capabilitySource: "USB Video Class 配置描述符",
    bestMode: { width: 1920, height: 1080, maximumFrameRate: 30, pixelFormat: "MJPEG" },
    nativeModes: [{ width: 1920, height: 1080, maximumFrameRate: 30, pixelFormat: "MJPEG" }]
  }
});
const cameraFunction = port({
  id: "camera-function",
  name: "HD Webcam",
  deviceId: String.raw`USB\VID_3333&PID_4444&MI_00\CAMERA`,
  nativeParentDeviceId: cameraId,
  busKind: "usb",
  hardwareKind: "Camera",
  pnpClass: "Camera",
  service: "usbvideo"
});
const internalMouse = port({
  id: "internal-mouse",
  name: "HID-compliant mouse",
  deviceId: String.raw`HID\UNIW0001&COL01\TOUCHPAD`,
  nativeParentDeviceId: String.raw`ACPI\UNIW0001\1`,
  nativeParentDisplayName: "I2C HID Device",
  hardwareKind: "Mouse",
  pnpClass: "Mouse",
  service: "mouhid",
  protocol: "HID"
});
const internalAudio = port({
  id: "internal-audio",
  name: "Internal Audio Codec",
  deviceId: String.raw`HDAUDIO\FUNC_01&VEN_14F1&DEV_1F87\CODEC`,
  nativeParentDeviceId: String.raw`PCI\HDAUDIO\CONTROLLER`,
  busKind: "audio",
  hardwareKind: "Audio Device",
  pnpClass: "MEDIA",
  service: "CnxtHdAudService",
  manufacturer: "Senary",
  protocol: "Audio",
  locationInfo: "Internal High Definition Audio Bus",
  idResolution: {
    database: "pci.ids",
    version: "test",
    vendorName: "Conexant Systems, Inc.",
    deviceName: "Test HD Audio Codec"
  }
});

const bluetoothAdapterId = String.raw`USB\VID_5555&PID_6666&MI_00\BLUETOOTH`;
const bluetoothAdapter = port({
  id: "bluetooth-adapter",
  name: "Bluetooth Adapter",
  deviceId: bluetoothAdapterId,
  nativeParentDeviceId: rootHubId,
  busKind: "usb",
  hardwareKind: "Bluetooth Device",
  pnpClass: "Bluetooth",
  service: "BTHUSB"
});
const bluetoothDevice = port({
  id: "bluetooth-device",
  name: "Wireless Headset",
  deviceId: String.raw`BTHENUM\DEV_AABBCCDDEEFF\HEADSET`,
  nativeParentDeviceId: bluetoothAdapterId,
  busKind: "bluetooth",
  hardwareKind: "Bluetooth Device",
  pnpClass: "Bluetooth",
  service: "BthA2dp"
});

const nvmeControllerId = String.raw`PCI\VEN_1E49&DEV_1081\NVME`;
const nvmeController = port({
  id: "nvme-controller",
  name: "Standard NVM Express Controller",
  deviceId: nvmeControllerId,
  nativeParentDeviceId: String.raw`PCI\ROOT_PORT\NVME`,
  busKind: "storage",
  hardwareKind: "Storage Device",
  pnpClass: "SCSIAdapter",
  service: "stornvme"
});
const nvmeDisk = port({
  id: "nvme-disk",
  name: "Internal NVMe",
  deviceId: String.raw`SCSI\DISK&VEN_NVME&PROD_INTERNAL\0`,
  nativeParentDeviceId: nvmeControllerId,
  busKind: "storage",
  hardwareKind: "Storage Device",
  pnpClass: "DiskDrive",
  service: "disk",
  storage: {
    physicalDeviceId: String.raw`\\.\PHYSICALDRIVE0`,
    model: "Internal NVMe",
    busType: "NVMe",
    capacityBytes: 1_024_000_000_000,
    partitionStyle: "GPT",
    healthStatus: "healthy",
    partitions: [],
    source: { domain: 5, code: 59, args: [] }
  }
});

const gpu = port({
  id: "gpu",
  name: "NVIDIA Test GPU",
  deviceId: String.raw`PCI\VEN_10DE&DEV_0001\GPU`,
  nativeParentDeviceId: String.raw`PCI\ROOT_PORT\GPU`,
  busKind: "display",
  hardwareKind: "Display Device",
  pnpClass: "Display",
  service: "nvlddmkm",
  manufacturer: "NVIDIA"
});
const wifi = port({
  id: "wifi",
  name: "Wi-Fi Adapter",
  deviceId: String.raw`PCI\VEN_14C3&DEV_0001\WIFI`,
  nativeParentDeviceId: String.raw`PCI\ROOT_PORT\WIFI`,
  busKind: "network",
  hardwareKind: "Network Adapter",
  pnpClass: "Net",
  service: "wifi",
  network: {
    interfaceName: "Wi-Fi",
    connectionState: "connected",
    transmitLinkSpeed: "2.4 Gbps",
    receiveLinkSpeed: "2.4 Gbps",
    permanentAddress: "00-11-22-33-44-55",
    activeMtuBytes: 1500,
    hardwareInterface: true,
    connectorPresent: true
  }
});
const panel = port({
  id: "panel",
  name: "Internal Panel",
  deviceId: String.raw`DISPLAY\TEST_PANEL\1`,
  busKind: "display",
  hardwareKind: "内置显示面板",
  connectorKind: "internal-display",
  pnpClass: "Monitor",
  display: {
    connectorTechnology: "Embedded DisplayPort",
    outputTechnology: "displayport-embedded",
    monitorName: "Internal Panel",
    resolution: "2560 x 1600",
    refreshRate: "240 Hz",
    active: true,
    targetAvailable: true,
    internal: true,
    connectorInstance: 0,
    bitsPerColorChannel: 10,
    advancedColorSupported: true,
    advancedColorEnabled: true,
    colorSpace: "RGB / PQ / BT.2020"
  }
});

const tree = buildInternalInterfaceTree([
  externalPort,
  externalFunction,
  usbController,
  rootHub,
  internalCamera,
  cameraFunction,
  internalMouse,
  internalAudio,
  bluetoothAdapter,
  bluetoothDevice,
  nvmeController,
  nvmeDisk,
  gpu,
  wifi,
  panel
]);

assert.ok(tree.interfaceCount >= 5);
assert.ok(tree.controllerCount >= 3);
assert.ok(!tree.nodes.some((node) => node.port.id === externalPort.id));
assert.ok(!tree.nodes.some((node) => node.port.id === externalFunction.id));

const diskNode = requiredNode("nvme-disk");
assert.equal(diskNode.specializedDevice?.kind, "external-storage");
assert.deepEqual(diskNode.path.slice(-2), ["Standard NVM Express Controller", "Internal NVMe"]);
assert.match(diskNode.path[0], /^PCIe 内部接口/);

assert.equal(requiredNode("internal-camera").specializedDevice?.kind, "camera");
assert.equal(requiredNode("internal-camera").title, "内置摄像头");
assert.equal(requiredNode("camera-function").specializedDevice?.kind, "camera");
assert.equal(requiredNode("camera-function").specializedDevice?.deviceTypeLabel, "摄像头视频功能");
assert.equal(requiredNode("internal-mouse").specializedDevice?.kind, "mouse");
assert.deepEqual(
  requiredNode("internal-mouse").specializedDevice?.summaryFields.map((field) => field.label),
  ["输入协议", "内部传输", "驱动服务", "设备状态"]
);
assert.equal(requiredNode("internal-mouse").specializedDevice?.internalFacts?.transport, "I2C HID");
assert.equal(requiredNode("internal-audio").specializedDevice?.kind, "audio-device");
assert.equal(requiredNode("internal-audio").specializedDevice?.deviceTypeLabel, "内置音频编解码器");
assert.equal(requiredNode("internal-audio").specializedDevice?.internalFacts?.catalogIdentity, "Test HD Audio Codec");
assert.equal(requiredNode("gpu").specializedDevice?.kind, "graphics-adapter");
assert.equal(requiredNode("gpu").specializedDevice?.internalFacts?.transport, "PCI Express");
assert.equal(requiredNode("wifi").specializedDevice?.kind, "network-adapter");
assert.equal(requiredNode("wifi").specializedDevice?.summaryFields[0].value, "Wi-Fi");
assert.equal(requiredNode("panel").specializedDevice?.kind, "monitor");
assert.equal(requiredNode("bluetooth-adapter").specializedDevice?.kind, "bluetooth-device");
assert.equal(requiredNode("bluetooth-device").specializedDevice?.kind, "bluetooth-device");
assert.equal(requiredNode("bluetooth-device").specializedDevice?.deviceTypeLabel, "Bluetooth 立体声音频");
assert.equal(requiredNode("nvme-controller").specializedDevice?.kind, "internal-controller");
assert.equal(requiredNode("nvme-controller").specializedDevice?.summaryFields[0].value, "NVMe 控制器");

const diskSearch = filterInternalInterfaceTree(tree.nodes, "Internal NVMe");
assert.deepEqual(diskSearch.map((node) => node.title).slice(-2), ["Standard NVM Express Controller", "Internal NVMe"]);
assert.match(diskSearch[0].title, /^PCIe 内部接口/);

const controllerSearch = filterInternalInterfaceTree(tree.nodes, "Standard NVM Express Controller");
assert.ok(controllerSearch.some((node) => node.title === "Internal NVMe"));

const detailsPageSource = readFileSync(new URL("../src/features/details/DetailsPage.tsx", import.meta.url), "utf8");
assert.match(detailsPageSource, /\{ id: "device", label: uiText\.[\w.]+ \}[\s\S]*\{ id: "gpu"[\s\S]*\{ id: "cpu"[\s\S]*\{ id: "report"/);
assert.doesNotMatch(detailsPageSource, /\{ id: "external", label: uiText\.deviceTopology\.externalScope \}|\{ id: "internal", label: uiText\.deviceTopology\.internalScope \}/);
const topologyViewSource = readFileSync(new URL("../src/features/deviceTopology/DeviceTopologyView.tsx", import.meta.url), "utf8");
assert.doesNotMatch(topologyViewSource, /device-port-section-tabs/);
assert.match(topologyViewSource, /device-scope-tabs[\s\S]*uiText\.deviceTopology\.externalScope[\s\S]*uiText\.deviceTopology\.internalScope/);

function requiredNode(portId: string) {
  const node = tree.nodes.find((candidate) => candidate.port.id === portId && candidate.role !== "internal-interface");
  assert.ok(node, `missing internal node ${portId}`);
  return node;
}

function port(options: Omit<Partial<DeviceTopologyPort>, "id" | "displayName" | "deviceId"> & {
  id: string;
  name: string;
  deviceId: string;
}): DeviceTopologyPort {
  return {
    id: options.id,
    isPhysicalConnector: options.isPhysicalConnector ?? false,
    displayName: options.name,
    connectorKind: options.connectorKind ?? "generic",
    busKind: options.busKind ?? "unknown",
    hardwareKind: options.hardwareKind ?? "Device",
    protocol: options.protocol ?? options.pnpClass ?? "Device",
    speed: options.speed ?? null,
    deviceId: options.deviceId,
    pnpClass: options.pnpClass,
    manufacturer: options.manufacturer,
    service: options.service,
    status: options.status ?? "OK",
    confidence: { domain: 5, code: 24, args: [] },
    source: { domain: 5, code: 35, args: [] },
    upstreamDeviceId: options.upstreamDeviceId,
    upstreamDisplayName: options.upstreamDisplayName,
    topologyPath: { domain: 5, code: 65, args: [] },
    nativeParentDeviceId: options.nativeParentDeviceId,
    nativeParentDisplayName: options.nativeParentDisplayName,
    locationInfo: options.locationInfo,
    locationPaths: options.locationPaths ?? [],
    classGuid: options.classGuid,
    display: options.display,
    network: options.network,
    idResolution: options.idResolution,
    advancedInterconnect: options.advancedInterconnect,
    usb: options.usb,
    hardwareIds: options.hardwareIds ?? [],
    compatibleIds: options.compatibleIds ?? [],
    hid: options.hid,
    camera: options.camera,
    smartDevice: options.smartDevice,
    storage: options.storage
  };
}

function usbConnection(id: string, userConnectable: boolean): DeviceTopologyUsbConnection {
  return {
    hubDevicePath: `hub:${id}`,
    portNumber: 1,
    deviceConnected: true,
    connectionStatus: { domain: 5, code: 47, args: [] },
    negotiatedSpeed: "USB 2.0 High-Speed / 480Mbps",
    deviceAddress: 1,
    deviceIsHub: false,
    supportedProtocols: "USB 2.0",
    portIsUserConnectable: userConnectable,
    companionPorts: [],
    deviceSpecification: "USB 2.0",
    deviceRevision: "1.0",
    deviceClass: "Per-interface class",
    productName: id === "external" ? "External Keyboard" : "Integrated Camera",
    interfaceProtocols: id === "external"
      ? ["HID Boot Keyboard [03/01/01]"]
      : ["Video Control [0E/01/00]", "Video Streaming [0E/02/00]"]
  };
}
