import assert from "node:assert/strict";
import test from "node:test";
import { capabilityLabels, hasAnyEvidence, hasUsbInterface, interfaceProtocols, usbDeviceClass } from "../src/features/deviceTopology/adapters/adapterEvidence.ts";
import { deviceAdapterOrder, resolveSpecializedDevice } from "../src/features/deviceTopology/adapters/adapterRegistry.ts";
import { audioDeviceAdapter } from "../src/features/deviceTopology/adapters/audioDeviceAdapter.ts";
import { cameraAdapter } from "../src/features/deviceTopology/adapters/cameraAdapter.ts";
import { externalStorageAdapter } from "../src/features/deviceTopology/adapters/externalStorageAdapter.ts";
import { genericDeviceAdapter } from "../src/features/deviceTopology/adapters/genericDeviceAdapter.ts";
import { keyboardAdapter, mouseAdapter } from "../src/features/deviceTopology/adapters/inputDeviceAdapters.ts";
import { networkAdapter } from "../src/features/deviceTopology/adapters/internalDeviceAdapters.ts";
import { mobileDeviceAdapter } from "../src/features/deviceTopology/adapters/mobileDeviceAdapter.ts";
import type { DeviceAdapterContext } from "../src/features/deviceTopology/adapters/types.ts";
import type { DeviceTopologyPort, DeviceTopologyUsbConnection, DeviceTopologyWirelessConnection } from "../src/types.ts";
import { applyLanguage, languageOptions, uiText } from "../src/text.ts";

type UsbInterface = NonNullable<DeviceTopologyUsbConnection["interfaces"]>[number];
const descriptor = (classCode: number, subClassCode = 0, protocolCode = 0): UsbInterface =>
  ({ interfaceNumber: 0, alternateSetting: 0, classCode, subClassCode, protocolCode });

function context(interfaces: UsbInterface[] | null = null): DeviceAdapterContext {
  const port: DeviceTopologyPort = {
    id: "numeric-fixture", displayName: "Fixture device", isPhysicalConnector: false,
    connectorKind: "usb-a", busKind: "usb", hardwareKind: "USB Device", protocol: "USB",
    deviceId: "fixture-device", locationPaths: [], hardwareIds: [], compatibleIds: [],
    confidence: { domain: 5, code: 24, args: [] }, source: { domain: 5, code: 35, args: [] },
    topologyPath: { domain: 5, code: 64, args: ["numeric-fixture"] },
    usb: {
      hubDevicePath: "fixture-hub", portNumber: 1, deviceConnected: true,
      connectionStatus: { domain: 5, code: 24, args: [] }, negotiatedSpeed: null,
      deviceAddress: 1, deviceIsHub: false, supportedProtocols: null, companionPorts: [],
      deviceSpecification: "", deviceRevision: "", deviceClass: "Audio Composite Mass Storage",
      interfaceProtocols: ["Audio Control [01/01/00]", "HID Boot Keyboard [03/01/01]", "MTP", "Mass Storage UAS"],
      deviceClassCode: null, deviceSubClassCode: null, deviceProtocolCode: null, interfaces
    }
  };
  return { scope: "external", port, fallbackTitle: port.displayName,
    downstreamInterfaceCount: 0, connectedDownstreamInterfaceCount: 0 };
}

test("USB numeric descriptors alone select the specialized adapter", () => {
  for (const [interfaces, expectedKind] of [
    [[descriptor(1, 1)], "audio-device"],
    [[descriptor(1, 2)], "audio-device"],
    [[descriptor(3, 1, 1)], "keyboard"],
    [[descriptor(3, 1, 2)], "mouse"],
    [[descriptor(14, 1)], "camera"],
    [[descriptor(14, 2)], "camera"],
    [[descriptor(8, 6, 98)], "external-storage"],
    [[descriptor(6, 1, 1), descriptor(2)], "mobile-device"],
    [[descriptor(6, 1, 1), descriptor(10)], "mobile-device"],
    [[descriptor(6, 1, 1), descriptor(255)], "mobile-device"]
  ] as const) {
    const input = context([...interfaces]);
    input.port.usb!.interfaceProtocols = ["unrelated legacy text"];
    assert.equal(resolveSpecializedDevice(input).kind, expectedKind);
  }
});

test("null or empty interface facts never classify from legacy English summaries", () => {
  for (const interfaces of [null, []]) {
    const input = context(interfaces);
    for (const adapter of [audioDeviceAdapter, keyboardAdapter, mouseAdapter, cameraAdapter, externalStorageAdapter, mobileDeviceAdapter]) {
      assert.equal(adapter.matches(input), false, adapter.id);
    }
    assert.equal(resolveSpecializedDevice(input).kind, "generic-device");
    assert.equal(hasUsbInterface(input.port, 1, 1), false);
    assert.equal(hasAnyEvidence(input.port, /Audio Control|Mass Storage|Composite/), false);
  }
});

test("USB presentation preserves unrecognized numeric codes and distinguishes missing descriptors", () => {
  const input = context([descriptor(222, 197, 199)]);
  input.port.usb!.deviceClassCode = 222;
  assert.deepEqual(interfaceProtocols(input.port), ["222/197/199"]);
  assert.deepEqual(capabilityLabels(input.port), ["222/197/199"]);
  assert.equal(usbDeviceClass(input.port), "222");
  assert.equal(genericDeviceAdapter.createModel(input).deviceClass, "222");
  input.port.usb!.interfaces = [];
  assert.deepEqual(interfaceProtocols(input.port), []);
  input.port.usb!.interfaces = null;
  input.port.usb!.deviceClassCode = null;
  assert.deepEqual(interfaceProtocols(input.port), [uiText.deviceTopology.deviceCategory.unknown]);
  assert.equal(usbDeviceClass(input.port), uiText.deviceTopology.deviceCategory.unknown);
});

test("audio, video and mobile capabilities read descriptor codes", () => {
  const input = context([descriptor(1, 1), descriptor(1, 2), descriptor(3), descriptor(14, 1), descriptor(14, 2),
    descriptor(6, 1, 1), descriptor(10), descriptor(255)]);
  const audio = audioDeviceAdapter.createModel(input);
  assert.equal(audio.audioControl, true);
  assert.equal(audio.audioStreaming, true);
  assert.equal(audio.hidControls, true);
  const camera = cameraAdapter.createModel(input);
  assert.equal(camera.videoControl, true);
  assert.equal(camera.videoStreaming, true);
  assert.equal(camera.audioCapable, true);
  const mobile = mobileDeviceAdapter.createModel(input);
  assert.equal(mobile.mediaTransfer, true);
  assert.equal(mobile.dataConnection, true);
  assert.equal(mobile.vendorChannel, true);
  input.port.usb!.interfaces = null;
  assert.equal(audioDeviceAdapter.createModel(input).audioControl, null);
  assert.equal(cameraAdapter.createModel(input).videoControl, null);
  assert.equal(mobileDeviceAdapter.createModel(input).mediaTransfer, null);
  input.port.usb!.interfaces = [descriptor(1, 3), descriptor(14, 3), descriptor(6, 1, 2)];
  assert.equal(audioDeviceAdapter.matches(input), false);
  assert.equal(cameraAdapter.matches(input), false);
  assert.equal(mobileDeviceAdapter.matches(input), false);
});

test("storage transport distinguishes UAS, BOT, unknown protocol and unavailable interfaces", () => {
  for (const [protocolCode, expected] of [[98, "UAS"], [80, "BOT"], [199, "8/6/199"]] as const) {
    assert.equal(externalStorageAdapter.createModel(context([descriptor(8, 6, protocolCode)])).transportMode, expected);
  }
  assert.equal(externalStorageAdapter.createModel(context()).transportMode, uiText.deviceTopology.deviceCategory.unknown);
});

test("composite classification requires its numeric device class and actual interfaces", () => {
  const input = context([descriptor(222)]);
  input.scope = "internal";
  input.port.displayName = "USB Composite Device";
  assert.equal(genericDeviceAdapter.createModel(input).deviceTypeLabel, uiText.deviceAdapters.internalDevice);
  input.port.usb!.deviceClassCode = 0;
  assert.equal(genericDeviceAdapter.createModel(input).deviceTypeLabel, uiText.deviceAdapters.usbCompositeDevice);
  input.port.usb!.deviceClassCode = 239;
  input.port.usb!.deviceSubClassCode = 2;
  input.port.usb!.deviceProtocolCode = 1;
  assert.equal(genericDeviceAdapter.createModel(input).deviceTypeLabel, uiText.deviceAdapters.usbCompositeDevice);
  input.port.usb!.interfaces = null;
  assert.equal(genericDeviceAdapter.createModel(input).deviceTypeLabel, uiText.deviceAdapters.internalDevice);
});

test("USB4 and GPU product names cannot create graphics-dock declarations", () => {
  const input = context();
  input.port.displayName = "Thunderbolt USB4 external GPU graphics adapter eGPU";
  input.port.advancedInterconnect = { kind: "usb4", technology: "Thunderbolt USB4",
    role: { domain: 5, code: 24, args: [] }, evidence: { domain: 5, code: 24, args: [] } };
  assert.equal(deviceAdapterOrder.includes("external-gpu-dock"), false);
  assert.notEqual(resolveSpecializedDevice(input).kind, "external-gpu-dock");
});

test("WLAN standards use negotiated facts, then supported facts, without parsing names", () => {
  const input = context();
  input.scope = "internal";
  input.port.usb = null;
  input.port.displayName = "Wi-Fi 7 Wireless 802.11be";
  input.port.protocol = "Wi-Fi 6E";
  input.port.network = { connectionState: "connected", wireless: null };
  const standard = () => networkAdapter.createModel(input).summaryFields
    .find(field => field.label === uiText.deviceAdapters.label.networkStandard)?.value;
  assert.equal(standard(), uiText.deviceTopology.deviceCategory.unknown);
  const wireless: DeviceTopologyWirelessConnection = {
    source: "wlan-api", interfaceStateRaw: 1, supportedPhyTypesRaw: [7, 8, 10],
    supportedStandards: ["802.11n", "802.11ac", "802.11ax"], negotiatedPhyTypeRaw: 8, negotiatedStandard: "802.11ac"
  };
  input.port.network.wireless = wireless;
  assert.equal(standard(), "802.11ac");
  wireless.negotiatedStandard = null;
  assert.equal(standard(), "802.11n / 802.11ac / 802.11ax");
  wireless.supportedStandards = null;
  wireless.negotiatedPhyTypeRaw = null;
  wireless.supportedPhyTypesRaw = null;
  assert.equal(standard(), uiText.deviceTopology.deviceCategory.unknown);
  wireless.supportedStandards = [];
  assert.equal(standard(), "[]");
  wireless.supportedStandards = null;
  wireless.negotiatedPhyTypeRaw = 99;
  assert.equal(standard(), `${uiText.deviceTopology.deviceCategory.unknown} (99)`);
});

test("unavailable USB and WLAN facts use localized unknown text in every selectable language", async () => {
  for (const { id } of languageOptions) {
    if (id === "system") continue;
    await applyLanguage(id);
    const input = context();
    assert.deepEqual(interfaceProtocols(input.port), [uiText.deviceTopology.deviceCategory.unknown], id);
    assert.equal(genericDeviceAdapter.createModel(input).deviceClass, uiText.deviceTopology.deviceCategory.unknown, id);
    input.port.network = { connectionState: "unknown", wireless: null };
    assert.equal(networkAdapter.createModel(input).summaryFields[0].value, uiText.deviceTopology.deviceCategory.unknown, id);
  }
});
