import assert from "node:assert/strict";
import test from "node:test";
import { attachedDisplay } from "../src/features/deviceTopology/deviceDetailSections.ts";
import { buildExternalInterfaceTree } from "../src/features/deviceTopology/externalInterfaceTree.ts";
import { describeDeviceTopologyNode, deviceTopologySummaryFields } from "../src/features/deviceTopology/deviceTopologyPresentation.ts";
import { resolveSpecializedDevice } from "../src/features/deviceTopology/adapters/adapterRegistry.ts";
import { uiText } from "../src/text.ts";
import type { DeviceTopologyPort } from "../src/types.ts";

function displayPort(active: boolean, targetAvailable = true, identity = true): DeviceTopologyPort {
  return {
    id: "display-port", isPhysicalConnector: true, displayName: identity ? "Fixture monitor" : "HDMI",
    connectorKind: "hdmi", busKind: "display", hardwareKind: "display-output", protocol: "HDMI",
    deviceId: "DISPLAY\\FIXTURE", locationPaths: [], hardwareIds: [], compatibleIds: [],
    confidence: { domain: 5, code: 24, args: [] }, source: { domain: 5, code: 35, args: [] },
    topologyPath: { domain: 5, code: 64, args: ["display-port"] },
    display: {
      connectorTechnology: "HDMI", outputTechnology: "hdmi", active, targetAvailable, internal: false,
      monitorName: identity ? "Fixture monitor" : null,
      monitorDevicePath: identity ? "monitor-path" : null,
      resolution: active ? "2560 x 1440" : null, refreshRate: active ? "120 Hz" : null
    }
  };
}

for (const active of [true, false]) {
  test(`an available display remains connected when output active=${active}`, () => {
    const port = displayPort(active);
    assert.equal(attachedDisplay(port), port.display);
    const tree = buildExternalInterfaceTree([port]);
    assert.equal(tree.attachedDeviceCount, 1);
    assert.equal(tree.nodes.length, 2);
    assert.ok(tree.nodes.every(node => node.connectionState === "connected"));
    const monitor = tree.nodes.find(node => node.role === "attached-device");
    assert.equal(monitor?.title, "Fixture monitor");
    assert.equal(monitor?.specializedDevice?.kind, "monitor");
    const presentation = describeDeviceTopologyNode(port);
    assert.equal(presentation.connectionState, "connected");
    assert.equal(presentation.connectionTarget, "Fixture monitor");
    assert.equal(deviceTopologySummaryFields(port).find(field => field.label === uiText.deviceTopology.label.connectionState)?.value,
      uiText.deviceTopology.connectionState.connected);
  });
}

test("a connected inactive monitor is identified without claiming active output", () => {
  const port = displayPort(false);
  const model = resolveSpecializedDevice({ scope: "external", port, fallbackTitle: "Fixture monitor",
    downstreamInterfaceCount: 0, connectedDownstreamInterfaceCount: 0 });
  assert.equal(model.kind, "monitor");
  if (model.kind !== "monitor") return;
  assert.notEqual(model.displayState, uiText.deviceAdapters.activeDisplayTarget);
});

test("an explicitly detached display with no identity has no attached monitor", () => {
  const port = displayPort(false, false, false);
  assert.equal(attachedDisplay(port), null);
  const tree = buildExternalInterfaceTree([port]);
  assert.equal(tree.attachedDeviceCount, 0);
  assert.equal(tree.nodes.length, 1);
  assert.equal(tree.nodes[0].connectionState, "disconnected");
  assert.equal(describeDeviceTopologyNode(port).connectionState, "disconnected");
});

test("cached monitor identity does not turn an unavailable target into a live connection", () => {
  const port = displayPort(false, false, true);
  assert.equal(attachedDisplay(port), port.display, "declared monitor details remain inspectable");
  const tree = buildExternalInterfaceTree([port]);
  assert.equal(tree.attachedDeviceCount, 0);
  assert.equal(tree.nodes.length, 1);
  assert.equal(tree.nodes[0].connectionState, "disconnected");
  assert.equal(describeDeviceTopologyNode(port).connectionState, "disconnected");
  const model = resolveSpecializedDevice({ scope: "external", port, fallbackTitle: "Fixture monitor",
    downstreamInterfaceCount: 0, connectedDownstreamInterfaceCount: 0 });
  assert.notEqual(model.kind, "monitor", "cached identity is not an attached monitor");
});
