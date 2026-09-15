import assert from "node:assert/strict";
import { deviceTopologyStateDecoder } from "../src/deviceTopology/deviceTopologyStateDecoder.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";

const port = {
  id: "port-1",
  isPhysicalConnector: true,
  displayName: "USB-C Port",
  connectorKind: "usb-c",
  busKind: "usb4",
  hardwareKind: "controller",
  protocol: "USB4",
  speed: "40 Gbps",
  physicalMaximumSpeed: "40 Gbps",
  deviceId: "DEVICE\\PORT_1",
  pnpClass: "USB",
  manufacturer: "Example",
  service: "usb4",
  status: "OK",
  confidence: { domain: 5, code: 24, args: [] },
  source: { domain: 5, code: 35, args: [] },
  upstreamDeviceId: null,
  upstreamDisplayName: null,
  topologyPath: "root/port-1",
  nativeParentDeviceId: null,
  nativeParentDisplayName: null,
  locationInfo: null,
  locationPaths: ["PCIROOT(0)#USBROOT(0)"],
  classGuid: null,
  display: {
    connectorTechnology: "displayport",
    monitorName: "Fixture Display",
    resolution: "2560x1440",
    refreshRate: "144 Hz",
    active: true,
    targetAvailable: true,
    internal: false,
    connectorInstance: 1,
    monitorDevicePath: null,
    bitsPerColorChannel: 10,
    colorEncoding: "RGB",
    advancedColorSupported: true,
    advancedColorEnabled: false,
    wideColorEnforced: false,
    sdrWhiteLevelNits: 203,
    hdrFormats: "HDR10",
    displayTechnology: "OLED",
    panelTechnology: "OLED",
    edidVersion: "1.4",
    edidProductName: "Fixture",
    edidSerialNumber: null,
    physicalSize: "600x340mm",
    minimumLuminanceNits: 0.01,
    maximumLuminanceNits: 1000,
    maximumFullFrameLuminanceNits: 250,
    colorCapabilitySource: "fixture",
    colorSpace: "BT.2020"
  },
  network: {
    interfaceName: "Ethernet",
    connectionState: "connected",
    transmitLinkSpeed: "1 Gbps",
    receiveLinkSpeed: "1 Gbps",
    permanentAddress: "00-11-22-33-44-55",
    activeMtuBytes: 1500,
    hardwareInterface: true,
    connectorPresent: true
  },
  idResolution: {
    database: "pci.ids",
    version: "1",
    vendorName: "Example",
    deviceName: "Controller",
    subsystemName: null
  },
  advancedInterconnect: {
    kind: "usb4-host-router",
    role: "host",
    technology: "USB4",
    evidence: "fixture"
  },
  usb: {
    hubDevicePath: "\\\\?\\usb#hub",
    portNumber: 1,
    deviceConnected: true,
    connectionStatus: "connected",
    negotiatedSpeed: "super-speed-plus",
    deviceAddress: 2,
    vendorId: "1234",
    productId: "5678",
    deviceIsHub: false,
    supportedProtocols: "USB 3.2",
    operatingAtSuperSpeedOrHigher: true,
    superSpeedCapableOrHigher: true,
    operatingAtSuperSpeedPlusOrHigher: true,
    superSpeedPlusCapableOrHigher: true,
    portIsUserConnectable: true,
    portIsDebugCapable: false,
    portHasMultipleCompanions: false,
    portConnectorIsTypeC: true,
    companionPorts: [{ companionIndex: 0, portNumber: 2, hubSymbolicLinkName: null }],
    deviceSpecification: "3.20",
    deviceRevision: "1.00",
    deviceClass: "00",
    manufacturerName: "Example",
    productName: "Fixture Device",
    serialNumber: null,
    interfaceProtocols: ["HID"],
    downstreamHubDevicePath: null,
    endpoints: [{
      interfaceNumber: 0,
      alternateSetting: 0,
      interfaceProtocol: "HID",
      endpointAddress: 129,
      direction: "in",
      transferType: "interrupt",
      maximumPacketSize: 64,
      interval: 1,
      serviceIntervalMicroseconds: 125,
      theoreticalReportRateHz: 8000
    }]
  },
  hardwareIds: ["USB\\VID_1234&PID_5678"],
  compatibleIds: ["USB\\Class_00"],
  devNodeStatus: 1,
  problemCode: null,
  hid: {
    hidType: "mouse",
    hidSpecification: "1.11",
    inputPollingIntervalMicroseconds: 125,
    theoreticalReportRateHz: 8000,
    reportedDpi: 1600,
    reportedScanRateHz: 8000,
    standardCapabilitySource: "descriptor",
    vendorCapabilitySource: null
  },
  camera: {
    capabilitySource: "media-foundation",
    bestMode: { width: 1920, height: 1080, maximumFrameRate: 60, pixelFormat: "NV12" },
    nativeModes: [{ width: 1280, height: 720, maximumFrameRate: 120, pixelFormat: "NV12" }]
  },
  smartDevice: {
    deviceType: "phone",
    manufacturer: "Example",
    model: "Fixture Phone",
    serialNumber: null,
    firmwareVersion: "1.0",
    protocol: "MTP",
    transport: "USB",
    batteryPercent: 80,
    storages: [{ name: "Internal", capacityBytes: 128_000_000_000, freeBytes: 64_000_000_000, fileSystem: null }],
    source: "fixture"
  },
  storage: {
    physicalDeviceId: "\\\\.\\PHYSICALDRIVE1",
    model: "Fixture Disk",
    manufacturer: "Example",
    serialNumber: null,
    firmwareRevision: "1.0",
    mediaType: "SSD",
    busType: "USB",
    capacityBytes: 1_000_000_000_000,
    bytesPerSector: 4096,
    partitionStyle: "GPT",
    healthStatus: "Healthy",
    partitions: [{
      deviceId: "Disk #1, Partition #1",
      partitionNumber: 1,
      type: "Basic",
      capacityBytes: 999_000_000_000,
      startingOffsetBytes: 1_048_576,
      bootable: false,
      bootPartition: false,
      primaryPartition: true,
      volumes: [{
        driveLetter: "E:",
        label: "Fixture",
        fileSystem: "NTFS",
        capacityBytes: 999_000_000_000,
        freeBytes: 500_000_000_000,
        mountState: "mounted",
        volumeSerialNumber: null
      }]
    }],
    source: "fixture"
  },
  ignored: true
};

const source = {
  schemaVersion: "4.0.0",
  state: "ready",
  snapshot: {
    capturedAt: "2026-08-22T15:20:30.000Z",
    system: {
      manufacturer: "Example",
      model: "Fixture PC",
      brandDisplayName: "Example",
      brandLogoText: "EX",
      biosVersion: null,
      baseBoardManufacturer: null,
      baseBoardProduct: null
    },
    ports: [port],
    notes: [{ domain: 5, code: 1, args: [] }]
  },
  contentGeneration: 4,
  stateRevision: 9,
  source: "live",
  lastSuccessAt: "2026-08-22T15:20:30.000Z",
  lastAttemptAt: "2026-08-22T15:20:30.000Z",
  failureCode: null,
  attemptDiagnostics: [],
  ignored: true
};

const decoded = deviceTopologyStateDecoder.decode(source);
assert.equal(decoded.schemaVersion, "4.0.0");
assert.equal(decoded.snapshot?.ports[0].usb?.endpoints?.[0].theoreticalReportRateHz, 8000);
assert.equal(decoded.snapshot?.ports[0].storage?.partitions[0].volumes[0].driveLetter, "E:");
assert.notEqual(decoded, source);
assert.notEqual(decoded.snapshot, source.snapshot);
assert.equal("ignored" in (decoded as unknown as Record<string, unknown>), false);
assert.equal(
  "ignored" in (decoded.snapshot!.ports[0] as unknown as Record<string, unknown>),
  false);

const invalidCases: Array<[unknown, string]> = [
  [{ ...source, schemaVersion: "1.0.0" }, "$.schemaVersion"],
  [{ ...source, state: "unknown" }, "$.state"],
  [{ ...source, source: "disk" }, "$.source"],
  [{ ...source, snapshot: null }, "$.snapshot"],
  [{ ...source, stateRevision: -1 }, "$.stateRevision"],
  [{ ...source, lastAttemptAt: "invalid" }, "$.lastAttemptAt"],
  [{ ...source, attemptDiagnostics: {} }, "$.attemptDiagnostics"],
  [{
    ...source,
    attemptDiagnostics: [{
      sourceId: "display-coordinator",
      status: "unknown",
      code: "device-topology-display-coordinator-incomplete",
      messageCode: { domain: 5, code: 10, args: ["Warming"] }
    }]
  }, "$.attemptDiagnostics[0].status"],
  [{
    ...source,
    snapshot: { ...source.snapshot, ports: [port, { ...port }] }
  }, "$.snapshot.ports[1].id"],
  [{
    ...source,
    snapshot: {
      ...source.snapshot,
      ports: [{ ...port, display: { ...port.display, colorSpace: undefined } }]
    }
  }, "$.snapshot.ports[0].display.colorSpace"],
  [{
    ...source,
    snapshot: {
      ...source.snapshot,
      ports: [{
        ...port,
        storage: { ...port.storage, capacityBytes: -1 }
      }]
    }
  }, "$.snapshot.ports[0].storage.capacityBytes"],
  [{
    ...source,
    snapshot: {
      ...source.snapshot,
      ports: [{ ...port, usb: { ...port.usb, endpoints: {} } }]
    }
  }, "$.snapshot.ports[0].usb.endpoints"]
];

for (const [value, expectedPath] of invalidCases) {
  assert.throws(
    () => deviceTopologyStateDecoder.decode(value),
    (error: unknown) => error instanceof ResponseDecodeError
      && error.path === expectedPath);
}

assert.deepEqual(deviceTopologyStateDecoder.decode({
  schemaVersion: "4.0.0",
  state: "warming",
  snapshot: null,
  contentGeneration: 0,
  stateRevision: 0,
  source: "memory",
  lastSuccessAt: null,
  lastAttemptAt: null,
  failureCode: null,
  attemptDiagnostics: []
}), {
  schemaVersion: "4.0.0",
  state: "warming",
  snapshot: null,
  contentGeneration: 0,
  stateRevision: 0,
  source: "memory",
  lastSuccessAt: null,
  lastAttemptAt: null,
  failureCode: null,
  attemptDiagnostics: []
});
