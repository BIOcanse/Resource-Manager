import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { deviceTopologyStateDecoder } from "../src/features/deviceTopology/deviceTopologyStateDecoder.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import { currentVolumeFreeBytes } from "../src/features/deviceTopology/deviceTopologyLiveInformation.ts";

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
  topologyPath: { domain: 5, code: 64, args: ["root/port-1"] },
  nativeParentDeviceId: null,
  nativeParentDisplayName: null,
  locationInfo: null,
  locationPaths: ["PCIROOT(0)#USBROOT(0)"],
  classGuid: null,
  display: {
    connectorTechnology: "displayport",
    outputTechnology: "displayport-external",
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
    displayTechnology: { domain: 5, code: 77, args: [] },
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
    connectorPresent: true,
    wireless: null
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
    role: { domain: 5, code: 43, args: [] },
    technology: "USB4",
    evidence: { domain: 5, code: 41, args: ["Usb4HostRouter"] }
  },
  usb: {
    hubDevicePath: "\\\\?\\usb#hub",
    portNumber: 1,
    deviceConnected: true,
    connectionStatus: { domain: 5, code: 47, args: [] },
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
    deviceClassCode: 3, deviceSubClassCode: 1, deviceProtocolCode: 1,
    interfaces: [{ interfaceNumber: 0, alternateSetting: 0, classCode: 3, subClassCode: 1, protocolCode: 1 }],
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
    standardCapabilitySource: { domain: 5, code: 60, args: [] },
    vendorCapabilitySource: null
  },
  camera: {
    capabilitySource: { domain: 5, code: 61, args: [] },
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
    source: { domain: 5, code: 63, args: [] }
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
    source: { domain: 5, code: 59, args: [] }
  },
  ignored: true
};

const source = {
  schemaVersion: "4.0.0",
  state: "ready",
  snapshot: {
    capturedAt: "2026-08-22T15:20:30.000Z",
    gpuInformation: [],
    externalMachines: null,
    machineIdentity: {
      formFactor: "mobile_workstation",
      evidence: {
        chassisTypes: { value: [10], reason: null },
        systemBatteryPresent: { value: true, reason: null },
        cpuModels: { value: ["Intel Core i9"], reason: null },
        gpuModels: { value: ["NVIDIA RTX PRO 6000"], reason: null },
        gpuAttachments: { value: [{ model: "NVIDIA RTX PRO 6000", isDetachable: false,
          adapterTypeFlags: 0, attachmentKind: null, evidenceSource: "d3dkmt-adapter-type" }], reason: null },
        workstationComponents: [{ component: "gpu", model: "NVIDIA RTX PRO 6000", ruleId: "nvidia-rtx-pro" }]
      }
    },
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
  informationFields: { observedAt: null, devices: [] }, attemptDiagnostics: [],
  ignored: true
};

const decoded = deviceTopologyStateDecoder.decode(source);
const wireless = { source: "wlan-api", interfaceStateRaw: 1, supportedPhyTypesRaw: [7, 10, 11],
  supportedStandards: ["802.11n", "802.11ax", "802.11be"], negotiatedPhyTypeRaw: 7, negotiatedStandard: "802.11n" };
const decodeWireless = (value: unknown) => deviceTopologyStateDecoder.decode({ ...source, snapshot: {
  ...source.snapshot, ports: [{ ...port, network: { ...port.network, wireless: value } }]
} }).snapshot?.ports[0].network?.wireless;
assert.deepEqual(decodeWireless(wireless), wireless);
assert.deepEqual(decodeWireless({ ...wireless, supportedPhyTypesRaw: [0xffffffff], supportedStandards: null,
  negotiatedPhyTypeRaw: null, negotiatedStandard: null }), { ...wireless, supportedPhyTypesRaw: [0xffffffff],
  supportedStandards: null, negotiatedPhyTypeRaw: null, negotiatedStandard: null });
for (const invalid of [undefined, { ...wireless, source: "adapter-name" }, { ...wireless, negotiatedPhyTypeRaw: -1 },
  { ...wireless, supportedPhyTypesRaw: [0x100000000] }]) assert.throws(() => decodeWireless(invalid), ResponseDecodeError);
assert.deepEqual(decoded.snapshot?.machineIdentity, source.snapshot.machineIdentity);
assert.equal(deviceTopologyStateDecoder.decode({ ...source, snapshot: { ...source.snapshot,
  ports: [{ ...port, usb: { ...port.usb, supportedProtocols: null } }]
} }).snapshot?.ports[0].usb?.supportedProtocols, null);
// The consumer preserves authoritative results even when raw facts could tempt it to infer a shape.
assert.equal(deviceTopologyStateDecoder.decode({ ...source, snapshot: { ...source.snapshot,
  machineIdentity: { ...source.snapshot.machineIdentity, formFactor: "unknown" }
} }).snapshot?.machineIdentity.formFactor, "unknown");
for (const invalid of [
  { ...source.snapshot.machineIdentity, formFactor: "notebook" },
  { ...source.snapshot.machineIdentity, evidence: { ...source.snapshot.machineIdentity.evidence,
    chassisTypes: { value: [128], reason: null } } },
  { ...source.snapshot.machineIdentity, evidence: { ...source.snapshot.machineIdentity.evidence,
    systemBatteryPresent: { value: null, reason: null } } }
]) {
  assert.throws(() => deviceTopologyStateDecoder.decode({ ...source,
    snapshot: { ...source.snapshot, machineIdentity: invalid } }), ResponseDecodeError);
}
assert.equal(decoded.schemaVersion, "4.0.0");
assert.equal(decoded.snapshot?.ports[0].usb?.endpoints?.[0].theoreticalReportRateHz, 8000);
assert.equal(decoded.snapshot?.ports[0].storage?.partitions[0].volumes[0].driveLetter, "E:");
assert.notEqual(decoded, source);
assert.notEqual(decoded.snapshot, source.snapshot);
assert.equal("ignored" in (decoded as unknown as Record<string, unknown>), false);
assert.equal(
  "ignored" in (decoded.snapshot!.ports[0] as unknown as Record<string, unknown>),
  false);

const information = {
  observedAt: "2026-08-22T15:20:30.000Z",
  devices: [{ deviceIdentity: "volume-guid-a", objectType: "volume", fields: [
    { definition: { name: "volume_all_mount_paths_reported" }, value: ["E:\\"] },
    { definition: { name: "volume_all_free_bytes_actual" }, value: "500" }
  ] }]
};
const current = deviceTopologyStateDecoder.decode({ ...source, informationFields: information });
assert.equal(currentVolumeFreeBytes(current.informationFields, "e:"), 500);
const changedInformation = structuredClone(information);
changedInformation.devices[0].fields[1].value = "450";
const changed = deviceTopologyStateDecoder.decode({ ...source, informationFields: changedInformation });
assert.equal(changed.contentGeneration, current.contentGeneration);
assert.equal(currentVolumeFreeBytes(changed.informationFields, "E:\\"), 450);
assert.equal(currentVolumeFreeBytes({ ...current.informationFields, observedAt: null }, "E:"), null);
assert.equal(currentVolumeFreeBytes(current.informationFields, "F:"), null);
assert.equal(currentVolumeFreeBytes(current.informationFields, null), null);
assert.equal(currentVolumeFreeBytes({ ...current.informationFields, devices: [
  ...current.informationFields.devices, { ...current.informationFields.devices[0], deviceIdentity: "other-volume" }
] }, "E:"), null);
for (const value of [null, "unavailable", "9007199254740993", -1]) {
  const unavailable = structuredClone(current.informationFields);
  unavailable.devices[0].fields[1].value = value;
  assert.equal(currentVolumeFreeBytes(unavailable, "E:"), null);
}

const invalidCases: Array<[unknown, string]> = [
  [{ ...source, informationFields: undefined }, "$.informationFields"],
  [{ ...source, informationFields: { ...information, observedAt: "bad-time" } }, "$.informationFields.observedAt"],
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
  informationFields: { observedAt: null, devices: [] }, attemptDiagnostics: []
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
  informationFields: { observedAt: null, devices: [] }, attemptDiagnostics: []
});

const edidWire = JSON.parse(readFileSync(new URL("./fixtures/displayEdid.json", import.meta.url), "utf8"));
const edidDisplay = deviceTopologyStateDecoder.decode(edidWire).snapshot!.ports[0].display!;
assert.equal(edidDisplay.currentRefreshRateNumerator, 60000); assert.equal(edidDisplay.currentRefreshRateDenominator, 1001);
assert.equal(edidDisplay.modeCapabilityDeclared!.modes.length, 7);
assert.equal(edidDisplay.modeCapabilityDeclared!.modes[5].nominalVerticalRateHz, null);
assert.equal(edidDisplay.modeCapabilityDeclared!.modes[6].cvtParameters!.deltaHorizontalBlankingCode, 6);
assert.equal(edidDisplay.edidRawDeclaredBase64, edidWire.snapshot.ports[0].display.edidRawDeclaredBase64);
const legacyEdidWire = structuredClone(edidWire);
for (const key of ["modeCapabilityDeclared", "edidRawDeclaredBase64", "currentWidth", "currentHeight", "currentRefreshRateNumerator", "currentRefreshRateDenominator", "currentBitsPerColorChannel"])
  delete legacyEdidWire.snapshot.ports[0].display[key];
const legacyDisplay = deviceTopologyStateDecoder.decode(legacyEdidWire).snapshot!.ports[0].display!;
assert.equal(legacyDisplay.modeCapabilityDeclared, null); assert.equal(legacyDisplay.edidRawDeclaredBase64, null); assert.equal(legacyDisplay.currentWidth, null);
const invalidCurrent = structuredClone(edidWire); invalidCurrent.snapshot.ports[0].display.currentWidth = 0x100000000;
assert.throws(() => deviceTopologyStateDecoder.decode(invalidCurrent), ResponseDecodeError);
const twoDisplays = structuredClone(edidWire); const otherPort = structuredClone(twoDisplays.snapshot.ports[0]);
otherPort.id = "fixture-hdmi-2"; otherPort.display.monitorDevicePath = "different-monitor-path"; otherPort.display.modeCapabilityDeclared = null;
twoDisplays.snapshot.ports.push(otherPort);
const exactPaths = deviceTopologyStateDecoder.decode(twoDisplays).snapshot!.ports;
assert.equal(exactPaths[0].display!.modeCapabilityDeclared!.modes.length, 7); assert.equal(exactPaths[1].display!.modeCapabilityDeclared, null);
assert.notEqual(exactPaths[0].display!.monitorDevicePath, exactPaths[1].display!.monitorDevicePath);

// A nullable port protocol must not invalidate its siblings or the snapshot.
const nullableProtocolWire = structuredClone(twoDisplays);
nullableProtocolWire.snapshot.ports[0].protocol = null;
const nullableProtocol = deviceTopologyStateDecoder.decode(nullableProtocolWire);
assert.equal(nullableProtocol.snapshot!.ports[0].protocol, null);
assert.deepEqual(nullableProtocol.snapshot!.ports[0], { ...exactPaths[0], protocol: null });
assert.deepEqual(nullableProtocol.snapshot!.ports[1], exactPaths[1]);
assert.deepEqual({ ...nullableProtocol, snapshot: null }, {
  ...deviceTopologyStateDecoder.decode(twoDisplays), snapshot: null
});
for (const invalidProtocol of [undefined, 42, {}, []]) {
  nullableProtocolWire.snapshot.ports[0].protocol = invalidProtocol;
  assert.throws(() => deviceTopologyStateDecoder.decode(nullableProtocolWire),
    (error: unknown) => error instanceof ResponseDecodeError
      && error.path === "$.snapshot.ports[0].protocol");
}

nullableProtocolWire.snapshot.ports[0].protocol = null;
nullableProtocolWire.snapshot.ports[0].connectorKind = null;
assert.throws(() => deviceTopologyStateDecoder.decode(nullableProtocolWire),
  (error: unknown) => error instanceof ResponseDecodeError
    && error.path === "$.snapshot.ports[0].connectorKind");

// 端口父子关系成环（自指、两个设备互为父、两个集线器互为下游）在解码边界失败，
// 指向闭合环的那个字段；无环的父子链照常通过。
const relationPort = (id: string, deviceId: string, extra: Record<string, unknown> = {}) => ({
  ...port, id, deviceId, nativeParentDeviceId: null, upstreamDeviceId: null,
  usb: { ...port.usb, hubDevicePath: String.raw`\\?\usb#hub-` + id, downstreamHubDevicePath: null },
  ...extra
});
const relationWire = (ports: unknown[]) => ({ ...source, snapshot: { ...source.snapshot, ports } });
const relationCycleCases: Array<[unknown[], string]> = [
  [[relationPort("self", String.raw`PCI\SELF`, { nativeParentDeviceId: "pci/self" })],
    "$.snapshot.ports[0].nativeParentDeviceId"],
  [[relationPort("a", String.raw`USB\A`, { upstreamDeviceId: String.raw`USB\B` }),
    relationPort("b", String.raw`USB\B`, { nativeParentDeviceId: String.raw`USB\A` })],
    "$.snapshot.ports[1].nativeParentDeviceId"],
  [[relationPort("hub-a", String.raw`USB\HUB_A`, { usb: { ...port.usb, hubDevicePath: String.raw`\\?\usb#hub-a`, downstreamHubDevicePath: String.raw`\\?\usb#hub-b` } }),
    relationPort("hub-b", String.raw`USB\HUB_B`, { usb: { ...port.usb, hubDevicePath: String.raw`\\?\usb#hub-b`, downstreamHubDevicePath: "USB#HUB-A" } })],
    "$.snapshot.ports[1].usb.downstreamHubDevicePath"]
];
for (const [ports, expectedPath] of relationCycleCases) {
  assert.throws(
    () => deviceTopologyStateDecoder.decode(relationWire(ports)),
    (error: unknown) => error instanceof ResponseDecodeError && error.path === expectedPath,
    expectedPath);
}
const relationChain = deviceTopologyStateDecoder.decode(relationWire([
  relationPort("controller", String.raw`PCI\CONTROLLER`),
  relationPort("root-hub", String.raw`USB\ROOT`, { nativeParentDeviceId: String.raw`PCI\CONTROLLER`, upstreamDeviceId: String.raw`PCI\CONTROLLER`,
    usb: { ...port.usb, hubDevicePath: String.raw`\\?\usb#root`, downstreamHubDevicePath: String.raw`\\?\usb#dock` } }),
  relationPort("dock", String.raw`USB\DOCK`, { nativeParentDeviceId: String.raw`USB\ROOT`, upstreamDeviceId: String.raw`PCI\CONTROLLER`,
    usb: { ...port.usb, hubDevicePath: String.raw`\\?\usb#dock`, downstreamHubDevicePath: null } })
]));
assert.equal(relationChain.snapshot?.ports.length, 3);
