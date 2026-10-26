import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import { startMockServer } from "./mockServerFixture.mjs";
import { deviceTopologyStateDecoder } from "../../../src/features/deviceTopology/deviceTopologyStateDecoder.ts";
import { networkPort } from "../deviceTopologyConnectionProbe.js";

let server;
before(async () => { server = await startMockServer(); });
after(async () => { await server?.close(); });

async function readSnapshot() {
  const { status, value } = await server.json("/api/device-topology/state");
  assert.equal(status, 200);
  const decoded = deviceTopologyStateDecoder.decode(value).snapshot;
  for (const field of ["machineIdentity", "gpuInformation", "externalMachines"])
    assert.deepEqual(decoded[field], value.snapshot[field], field);
  for (let index = 0; index < decoded.ports.length; index++) {
    assert.deepEqual(decoded.ports[index].usb, value.snapshot.ports[index].usb, `ports[${index}].usb`);
    assert.deepEqual(decoded.ports[index].network, value.snapshot.ports[index].network, `ports[${index}].network`);
  }
  return decoded;
}

test("all machine scenarios keep the production wire shape", async () => {
  for (const form of ["workstation", "mobile_workstation", "laptop", "desktop", "unknown"]) {
    await server.scenario({ deviceTopologyMachineIdentity: form });
    const snapshot = await readSnapshot();
    assert.equal(snapshot.machineIdentity.formFactor, form);
    if (form === "unknown") assert.deepEqual(snapshot.machineIdentity.evidence.chassisTypes,
      { value: null, reason: "source-unavailable" });
  }
});

test("dock, peer, numeric USB and WLAN scenarios all pass the production decoder", async () => {
  await server.scenario({ deviceTopologyMachineIdentity: "laptop", deviceTopologyGraphicsDock: true,
    deviceTopologyExternalMachine: true, deviceTopologyNumericUsb: true, deviceTopologyWireless: "standards" });
  const snapshot = await readSnapshot();
  assert.equal(snapshot.machineIdentity.formFactor, "laptop");
  assert.equal(snapshot.gpuInformation[0].attachmentKind, "graphics_dock");
  assert.equal(snapshot.externalMachines[0].model, null);
  assert.deepEqual(snapshot.externalMachines[0].components, [{ component: "soc", model: null }]);
  assert.equal(snapshot.ports.find(port => port.id === "fixture-wireless").network.wireless.negotiatedStandard, "802.11ac");
  await server.scenario({ deviceTopologyWireless: "unknown" });
  assert.equal((await readSnapshot()).ports.find(port => port.id === "fixture-wireless").network.wireless.supportedStandards, null);
});

test("network connection journey supplies explicit nullable wireless facts", async () => {
  await server.scenario({ deviceTopologyGraphicsDock: false, deviceTopologyExternalMachine: false,
    deviceTopologyNumericUsb: false, deviceTopologyWireless: null });
  for (const state of ["connected", "disconnected", "unknown", "not-reported"]) {
    await server.scenario({ deviceTopologyPorts: [networkPort(state)] });
    const snapshot = await readSnapshot();
    assert.equal(snapshot.ports[0].network.connectionState, state);
    assert.equal(snapshot.ports[0].network.wireless, null);
  }
});
