import assert from "node:assert/strict";
import test from "node:test";
import { internalDeviceFacts } from "../src/features/deviceTopology/adapters/adapterEvidence.ts";
import { resolveSpecializedDevice } from "../src/features/deviceTopology/adapters/adapterRegistry.ts";
import { deviceTopologySummaryFields } from "../src/features/deviceTopology/deviceTopologyPresentation.ts";
import type { DeviceAdapterContext } from "../src/features/deviceTopology/adapters/types.ts";
import type { DeviceTopologyPort } from "../src/types.ts";
import { uiText } from "../src/text.ts";

function context(transport: DeviceTopologyPort["transport"], busKind: string, deviceId: string): DeviceAdapterContext {
  const port: DeviceTopologyPort = {
    transport,
    id: "graphics-adapter", displayName: "Fixture graphics adapter", isPhysicalConnector: false,
    connectorKind: "internal", busKind, hardwareKind: "Graphics Adapter", protocol: busKind,
    speed: null, pnpClass: "Display", deviceId, locationPaths: [], hardwareIds: [], compatibleIds: [],
    confidence: { domain: 5, code: 24, args: [] }, source: { domain: 5, code: 35, args: [] },
    topologyPath: { domain: 5, code: 64, args: ["graphics-adapter"] }
  };
  return { scope: "internal", port, fallbackTitle: port.displayName!,
    downstreamInterfaceCount: 0, connectedDownstreamInterfaceCount: 0 };
}

function knownTransport(code: NonNullable<DeviceTopologyPort["transport"]["code"]>): DeviceTopologyPort["transport"] {
  return { code, reason: null, ruleId: "fixture-authoritative-transport", evidence: [{
    source: "setupapi-cfgmgr32", deviceId: "FIXTURE\\transport", depth: 0,
    enumeratorName: null, busTypeGuid: null, service: null, parentDeviceId: null
  }] };
}

for (const deviceId of [String.raw`USB\VID_1234&PID_5678\GPU`, String.raw`PCI\VEN_1234&DEV_5678\GPU`]) {
  test(`backend USB transport wins over the Display class and PnP evidence: ${deviceId}`, () => {
    const input = context(knownTransport("usb"), "display", deviceId);
    assert.equal(internalDeviceFacts(input)?.transport, "USB");
    const model = resolveSpecializedDevice(input);
    assert.ok(model.kind === "graphics-adapter");
    assert.equal(model.internalFacts?.transport, "USB");
    assert.equal(model.busType, "USB");
    assert.match(model.subtitle, /USB/);
    assert.doesNotMatch(model.subtitle, /PCI Express/);
  });
}

test("plain PCI graphics transport never implies PCI Express", () => {
  const input = context(knownTransport("pci"), "usb", String.raw`PCI\VEN_1234&DEV_5678\GPU`);
  input.port.service = "BTHUSB";
  input.port.displayName = "PCI Express USB4 graphics adapter";
  assert.equal(internalDeviceFacts(input)?.transport, "PCI");
  const model = resolveSpecializedDevice(input);
  assert.ok(model.kind === "graphics-adapter");
  assert.equal(model.internalFacts?.transport, "PCI");
  assert.equal(model.busType, "PCI");
  assert.doesNotMatch(model.subtitle, /PCI Express|USB4/);
  input.port.busKind = "pci";
  assert.equal(deviceTopologySummaryFields(input.port).find(field => field.label === uiText.deviceTopology.label.bus)?.value, "PCI");
});

test("null transport retains the backend reason despite USB and NVMe-looking raw facts", () => {
  const input = context({ code: null, reason: "parent-chain-incomplete", ruleId: null, evidence: [] },
    "usb", String.raw`PCI\VEN_1234&DEV_5678\GPU`);
  input.port.service = "stornvme";
  input.port.protocol = "PCI Express";
  input.port.nativeParentDisplayName = "USB NVMe controller";
  assert.equal(internalDeviceFacts(input)?.transport, `${uiText.deviceTopology.deviceCategory.unknown} (parent-chain-incomplete)`);
  assert.equal(resolveSpecializedDevice(input).internalFacts?.transport, `${uiText.deviceTopology.deviceCategory.unknown} (parent-chain-incomplete)`);
});

test("parent-chain evidence remains ordered and cannot override the authoritative child transport", () => {
  const transport = knownTransport("acpi");
  transport.evidence[0].parentDeviceId = "PCI\\parent";
  transport.evidence.push({ source: "setupapi-cfgmgr32", deviceId: "PCI\\parent", depth: 1,
    enumeratorName: "PCI", busTypeGuid: null, service: "usbxhci", parentDeviceId: null });
  const input = context(transport, "display", "USB\\misleading-child");
  const original = structuredClone(transport);
  assert.equal(resolveSpecializedDevice(input).internalFacts?.transport, "ACPI");
  assert.deepEqual(input.port.transport, original);
});
