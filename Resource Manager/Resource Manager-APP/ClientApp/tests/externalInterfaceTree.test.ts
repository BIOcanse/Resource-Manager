import assert from "node:assert/strict";
import {
  buildExternalInterfaceTree,
  filterExternalInterfaceTree
} from "../src/deviceTopology/externalInterfaceTree.ts";
import { deviceTopologySummaryFields } from "../src/deviceTopology/deviceTopologyPresentation.ts";
import { resolveSpecializedDevice } from "../src/deviceTopology/adapters/adapterRegistry.ts";
import type { DeviceTopologyPort, DeviceTopologyUsbConnection } from "../src/types.ts";

const rootHubPath = String.raw`\\?\usb#root_hub30#root`;
const dockHubPath = String.raw`\\?\usb#vid_1234&pid_5678#dock`;
const host = usbPort({
  id: "host-port",
  name: "Example Dock",
  connectorKind: "usb-c",
  hubPath: rootHubPath,
  portNumber: 2,
  connected: true,
  isHub: true,
  downstreamHubPath: String.raw`USB#VID_1234&PID_5678#dock`,
  productName: "Example Dock"
});
const dockPorts = [
  usbPort({
    id: "dock-port-1",
    name: "Gaming Mouse",
    connectorKind: "usb-a",
    hubPath: dockHubPath,
    portNumber: 1,
    connected: true,
    productName: "Gaming Mouse",
    interfaceProtocols: ["HID Boot Mouse [03/01/02]"],
    hid: {
      hidType: "鼠标",
      hidSpecification: "HID 1.11",
      inputPollingIntervalMicroseconds: 1_000,
      theoreticalReportRateHz: 1_000,
      standardCapabilitySource: "USB HID + endpoint descriptor"
    }
  }),
  usbPort({
    id: "dock-port-2",
    name: "Portable SSD",
    connectorKind: "usb-a",
    hubPath: dockHubPath,
    portNumber: 2,
    connected: true,
    productName: "Portable SSD",
    interfaceProtocols: ["Mass Storage / SCSI / UAS [08/06/62]"],
    storage: {
      physicalDeviceId: String.raw`\\.\PHYSICALDRIVE1`,
      model: "Portable SSD",
      mediaType: "External hard disk media",
      busType: "USB",
      capacityBytes: 1_000_204_886_016,
      bytesPerSector: 512,
      partitionStyle: "GPT",
      healthStatus: "正常",
      source: "MSFT_Disk + Win32 磁盘/分区/卷关联",
      partitions: [{
        deviceId: "Disk #1, Partition #0",
        partitionNumber: 1,
        type: "GPT: Basic Data",
        capacityBytes: 1_000_202_788_864,
        startingOffsetBytes: 1_048_576,
        primaryPartition: true,
        volumes: [{
          driveLetter: "F:",
          label: "Portable",
          fileSystem: "exFAT",
          capacityBytes: 1_000_185_827_328,
          freeBytes: 750_000_000_000,
          mountState: "已挂载"
        }]
      }]
    }
  }),
  usbPort({ id: "dock-port-3", connectorKind: "usb-a", hubPath: dockHubPath, portNumber: 3 }),
  usbPort({ id: "dock-port-4", connectorKind: "usb-a", hubPath: dockHubPath, portNumber: 4 })
];

const tree = buildExternalInterfaceTree([host, ...dockPorts]);
assert.equal(tree.hostInterfaceCount, 1);
assert.equal(tree.downstreamInterfaceCount, 4);
assert.equal(tree.attachedDeviceCount, 3);
assert.equal(tree.nodes.length, 8);

const dock = tree.nodes.find((node) => node.role === "external-hub");
assert.ok(dock);
assert.equal(dock.title, "Example Dock");
assert.equal(dock.specializedDevice?.kind, "dock");
assert.equal(dock.specializedDevice?.iconKind, "dock");
assert.equal(dock.depth, 1);
assert.equal(dock.parentId, "external:host-port:connector");

const downstream = tree.nodes.filter((node) => node.role === "downstream-interface");
assert.deepEqual(downstream.map((node) => node.port.usb?.portNumber), [1, 2, 3, 4]);
assert.ok(downstream.every((node) => node.parentId === dock.id && node.depth === 2));

const mouse = tree.nodes.find((node) => node.title === "Gaming Mouse");
assert.ok(mouse);
assert.equal(mouse.specializedDevice?.kind, "mouse");
assert.deepEqual(mouse.path, ["USB-C 接口", "Example Dock", "USB-A 接口 1", "Gaming Mouse"]);

const storage = tree.nodes.find((node) => node.title === "Portable SSD");
assert.equal(storage?.specializedDevice?.kind, "external-storage");
assert.equal(storage?.specializedDevice?.summaryFields[2]?.value, "1 个");
assert.equal(storage?.specializedDevice?.summaryFields[3]?.value, "正常");

const internalDisk = usbPort({
  id: "internal-disk",
  name: "Internal NVMe",
  connectorKind: "pcie",
  hubPath: "none",
  portNumber: 0,
  storage: {
    physicalDeviceId: String.raw`\\.\PHYSICALDRIVE0`,
    model: "Internal NVMe",
    busType: "NVMe",
    capacityBytes: 1_024_000_000_000,
    partitionStyle: "GPT",
    healthStatus: "正常",
    partitions: [],
    source: "MSFT_Disk"
  }
});
internalDisk.usb = undefined;
internalDisk.isPhysicalConnector = false;
const internalDiskModel = resolveSpecializedDevice({
  scope: "internal",
  port: internalDisk,
  fallbackTitle: internalDisk.displayName,
  downstreamInterfaceCount: 0,
  connectedDownstreamInterfaceCount: 0
});
assert.equal(internalDiskModel.deviceTypeLabel, "磁盘");
assert.equal(internalDiskModel.badge, "磁盘");
assert.match(internalDiskModel.subtitle, /NVMe/);
assert.doesNotMatch(internalDiskModel.subtitle, /USB Mass Storage/);

assert.equal(mouse.specializedDevice?.summaryFields[1]?.value, "1 ms");
assert.equal(mouse.specializedDevice?.summaryFields[2]?.value, "1,000 Hz");
assert.equal(mouse.specializedDevice?.summaryFields[3]?.value, "标准 HID 未报告");

const mouseSearch = filterExternalInterfaceTree(tree.nodes, "gaming mouse");
assert.deepEqual(mouseSearch.map((node) => node.title), mouse.path);

const dockSearch = filterExternalInterfaceTree(tree.nodes, "example dock");
assert.equal(dockSearch.length, tree.nodes.length);

const display = displayPort();
const displayTree = buildExternalInterfaceTree([display]);
assert.equal(displayTree.hostInterfaceCount, 1);
assert.equal(displayTree.downstreamInterfaceCount, 0);
assert.equal(displayTree.attachedDeviceCount, 1);
assert.equal(displayTree.nodes.length, 2);

const displayInterface = displayTree.nodes.find((node) => node.role === "host-interface");
const monitor = displayTree.nodes.find((node) => node.role === "attached-device");
assert.ok(displayInterface);
assert.ok(monitor);
assert.equal(displayInterface.title, "DisplayPort 接口");
assert.equal(displayInterface.subtitle, "已连接");
assert.equal(monitor.title, "P275MS PLUS");
assert.equal(monitor.specializedDevice?.kind, "monitor");
assert.equal(monitor.iconKind, "monitor");
assert.equal(monitor.parentId, displayInterface.id);
assert.deepEqual(monitor.path, ["DisplayPort 接口", "P275MS PLUS"]);
assert.deepEqual(
  filterExternalInterfaceTree(displayTree.nodes, "P275MS PLUS").map((node) => node.title),
  ["DisplayPort 接口", "P275MS PLUS"]
);

const interfaceSummary = deviceTopologySummaryFields(display);
assert.deepEqual(interfaceSummary, [
  { label: "接口类型", value: "DisplayPort 接口" },
  { label: "连接状态", value: "已连接" },
  { label: "协议", value: "DisplayPort" }
]);
const monitorSummary = monitor.specializedDevice?.summaryFields;
assert.deepEqual(monitorSummary, [
  { label: "当前模式", value: "2560 x 1440 · 240 Hz" },
  { label: "HDR / 高级颜色", value: "HDR10 / PQ · 已开启" },
  { label: "输出位深", value: "10 bit / 色通道" },
  { label: "显示技术", value: "数字显示 / DisplayPort" }
]);

const classifiedTree = buildExternalInterfaceTree([
  usbPort({
    id: "keyboard-port",
    name: "HERO 99 HE",
    connectorKind: "usb-a",
    hubPath: rootHubPath,
    portNumber: 5,
    connected: true,
    productName: "HERO 99 HE",
    interfaceProtocols: ["HID Boot Keyboard [03/01/01]", "HID [03/00/00]"],
    hid: {
      hidType: "键盘",
      hidSpecification: "HID 1.11",
      inputPollingIntervalMicroseconds: 1_000,
      theoreticalReportRateHz: 1_000,
      standardCapabilitySource: "USB HID + endpoint descriptor"
    }
  }),
  usbPort({
    id: "camera-port",
    name: "HIK 2K Camera",
    connectorKind: "usb-a",
    hubPath: rootHubPath,
    portNumber: 6,
    connected: true,
    productName: "HIK 2K Camera",
    interfaceProtocols: [
      "Video Control [0E/01/00]",
      "Video Streaming [0E/02/00]",
      "Audio Control [01/01/00]",
      "Audio Streaming [01/02/00]"
    ],
    camera: {
      capabilitySource: "USB Video Class native descriptors",
      bestMode: { width: 2560, height: 1440, maximumFrameRate: 30, pixelFormat: "MJPEG" },
      nativeModes: [
        { width: 2560, height: 1440, maximumFrameRate: 30, pixelFormat: "MJPEG" },
        { width: 1920, height: 1080, maximumFrameRate: 60, pixelFormat: "MJPEG" }
      ]
    }
  }),
  usbPort({
    id: "audio-port",
    name: "USB Audio Device",
    connectorKind: "usb-a",
    hubPath: rootHubPath,
    portNumber: 7,
    connected: true,
    productName: "USB Audio Device",
    interfaceProtocols: ["Audio Control [01/01/00]", "Audio Streaming [01/02/00]", "HID [03/00/00]"]
  }),
  usbPort({
    id: "mobile-port",
    name: "SAMSUNG_Android",
    connectorKind: "usb-c",
    hubPath: rootHubPath,
    portNumber: 8,
    connected: true,
    productName: "SAMSUNG_Android",
    interfaceProtocols: ["Still Image [06/01/01]", "CDC Control [02/02/01]", "CDC Data [0A/00/00]"],
    smartDevice: {
      deviceType: "手机",
      manufacturer: "Samsung",
      model: "SM-N981U",
      protocol: "MTP",
      transport: "USB",
      storages: [],
      source: "Windows Portable Devices / MTP"
    }
  }),
  usbPort({
    id: "hub-port",
    name: "USB 2.0 Hub",
    connectorKind: "usb-a",
    hubPath: rootHubPath,
    portNumber: 9,
    connected: true,
    isHub: true,
    downstreamHubPath: String.raw`USB#VID_9876&PID_5432#hub`,
    productName: "USB 2.0 Hub",
    interfaceProtocols: ["Hub [09/00/00]"]
  }),
  usbPort({
    id: "bus-powered-port",
    name: "USB Light",
    connectorKind: "usb-c",
    hubPath: rootHubPath,
    portNumber: 10,
    connected: true,
    productName: "USB Light",
    interfaceProtocols: ["Vendor Specific [FF/00/00]"]
  })
]);

assert.equal(deviceKind(classifiedTree, "HERO 99 HE"), "keyboard");
assert.equal(deviceKind(classifiedTree, "HIK 2K Camera"), "camera");
assert.equal(deviceKind(classifiedTree, "USB Audio Device"), "audio-device");
assert.equal(deviceKind(classifiedTree, "SAMSUNG_Android"), "mobile-device");
assert.equal(deviceKind(classifiedTree, "USB 2.0 Hub"), "usb-hub");
assert.equal(deviceKind(classifiedTree, "USB Light"), "generic-device");
const camera = classifiedTree.nodes.find((node) => node.title === "HIK 2K Camera");
assert.equal(camera?.specializedDevice?.summaryFields[0]?.value, "2560 x 1440 @ 30 fps · MJPEG");
const mobile = classifiedTree.nodes.find((node) => node.title === "SAMSUNG_Android");
assert.equal(mobile?.specializedDevice?.summaryFields[1]?.value, "Samsung · SM-N981U");
assert.ok(classifiedTree.nodes.every((node) => node.specializedDevice?.kind !== "external-gpu-dock"));
assert.ok(classifiedTree.nodes.every((node) => node.specializedDevice?.kind !== "power-input"));

function deviceKind(tree: ReturnType<typeof buildExternalInterfaceTree>, title: string) {
  return tree.nodes.find((node) => node.title === title)?.specializedDevice?.kind;
}

function displayPort(): DeviceTopologyPort {
  return {
    id: "display-port",
    isPhysicalConnector: true,
    displayName: "P275MS PLUS",
    connectorKind: "displayport",
    busKind: "display",
    hardwareKind: "Display Path",
    protocol: "DisplayPort",
    speed: "DisplayPort 2.1",
    deviceId: "DISPLAY\\P275MSPLUS",
    confidence: { domain: 5, code: 24, args: [] },
    source: { domain: 5, code: 35, args: [] },
    topologyPath: "test",
    locationPaths: [],
    hardwareIds: [],
    compatibleIds: [],
    display: {
      connectorTechnology: "DisplayPort",
      monitorName: "P275MS PLUS",
      resolution: "2560 x 1440",
      refreshRate: "240 Hz",
      active: true,
      targetAvailable: true,
      internal: false,
      connectorInstance: 0,
      bitsPerColorChannel: 10,
      colorEncoding: "RGB",
      colorSpace: "RGB / PQ / BT.2020",
      advancedColorSupported: true,
      advancedColorEnabled: true,
      hdrFormats: "HDR10 / PQ",
      displayTechnology: "数字显示 / DisplayPort",
      edidVersion: "EDID 1.4",
      physicalSize: "600 x 340 mm",
      maximumLuminanceNits: 1_000
    }
  };
}

function usbPort(options: {
  id: string;
  name?: string;
  connectorKind: string;
  hubPath: string;
  portNumber: number;
  connected?: boolean;
  isHub?: boolean;
  downstreamHubPath?: string;
  productName?: string;
  interfaceProtocols?: string[];
  hid?: DeviceTopologyPort["hid"];
  camera?: DeviceTopologyPort["camera"];
  smartDevice?: DeviceTopologyPort["smartDevice"];
  storage?: DeviceTopologyPort["storage"];
}): DeviceTopologyPort {
  return {
    id: options.id,
    isPhysicalConnector: true,
    displayName: options.name ?? `空闲 ${options.connectorKind} 接口`,
    connectorKind: options.connectorKind,
    busKind: "usb",
    hardwareKind: options.isHub ? "USB Hub" : "USB Device",
    protocol: "USB 3.x",
    speed: options.connected ? "USB 3.x SuperSpeed / 5Gbps+" : "未连接",
    deviceId: `USBPORT\\${options.id}`,
    confidence: { domain: 5, code: 24, args: [] },
    source: { domain: 5, code: 35, args: [] },
    topologyPath: "test",
    locationPaths: [],
    hardwareIds: [],
    compatibleIds: [],
    usb: usbConnection(options),
    hid: options.hid,
    camera: options.camera,
    smartDevice: options.smartDevice,
    storage: options.storage
  };
}

function usbConnection(options: {
  hubPath: string;
  portNumber: number;
  connected?: boolean;
  isHub?: boolean;
  downstreamHubPath?: string;
  productName?: string;
  interfaceProtocols?: string[];
}): DeviceTopologyUsbConnection {
  return {
    hubDevicePath: options.hubPath,
    portNumber: options.portNumber,
    deviceConnected: options.connected === true,
    connectionStatus: options.connected ? "已连接" : "未连接",
    negotiatedSpeed: options.connected ? "USB 3.x SuperSpeed / 5Gbps+" : "未连接",
    deviceAddress: options.portNumber,
    deviceIsHub: options.isHub === true,
    supportedProtocols: "USB 2.0 / USB 3.x",
    portIsUserConnectable: true,
    companionPorts: [],
    deviceSpecification: "USB 3.2",
    deviceRevision: "1.0",
    deviceClass: options.isHub ? "Hub" : "Device",
    productName: options.productName,
    interfaceProtocols: options.interfaceProtocols ?? [],
    downstreamHubDevicePath: options.downstreamHubPath
  };
}
