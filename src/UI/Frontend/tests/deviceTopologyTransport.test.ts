import assert from "node:assert/strict";
import test from "node:test";
import { internalDeviceFacts } from "../src/features/deviceTopology/adapters/adapterEvidence.ts";
import { resolveSpecializedDevice } from "../src/features/deviceTopology/adapters/adapterRegistry.ts";
import type { DeviceAdapterContext } from "../src/features/deviceTopology/adapters/types.ts";
import type { DeviceTopologyPort } from "../src/types.ts";

function context(busKind: string, deviceId: string): DeviceAdapterContext {
  const port: DeviceTopologyPort = {
    id: "graphics-adapter", displayName: "Fixture graphics adapter", isPhysicalConnector: false,
    connectorKind: "internal", busKind, hardwareKind: "Graphics Adapter", protocol: busKind,
    pnpClass: "Display", deviceId, locationPaths: [], hardwareIds: [], compatibleIds: [],
    confidence: { domain: 5, code: 24, args: [] }, source: { domain: 5, code: 35, args: [] },
    topologyPath: { domain: 5, code: 64, args: ["graphics-adapter"] }
  };
  return { scope: "internal", port, fallbackTitle: port.displayName!,
    downstreamInterfaceCount: 0, connectedDownstreamInterfaceCount: 0 };
}

for (const deviceId of [String.raw`USB\VID_1234&PID_5678\GPU`, String.raw`PCI\VEN_1234&DEV_5678\GPU`]) {
  test(`backend USB bus wins over the Display class and PnP evidence: ${deviceId}`, () => {
    const input = context("usb", deviceId);
    assert.equal(internalDeviceFacts(input)?.transport, "USB");
    const model = resolveSpecializedDevice(input);
    assert.equal(model.kind, "graphics-adapter");
    assert.equal(model.internalFacts?.transport, "USB");
    assert.match(model.subtitle, /USB/);
    assert.doesNotMatch(model.subtitle, /PCI Express/);
  });
}

test("a native PCI graphics adapter retains its existing PCI Express transport", () => {
  const input = context("display", String.raw`PCI\VEN_1234&DEV_5678\GPU`);
  assert.equal(internalDeviceFacts(input)?.transport, "PCI Express");
  assert.equal(resolveSpecializedDevice(input).internalFacts?.transport, "PCI Express");
});
