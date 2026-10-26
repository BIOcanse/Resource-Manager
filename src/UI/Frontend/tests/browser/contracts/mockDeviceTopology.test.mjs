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
    assert.deepEqual(decoded.ports[index].transport, value.snapshot.ports[index].transport, `ports[${index}].transport`);
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
  assert.equal(snapshot.ports.find(port => port.id === "fixture-peer-link").transport.code, "usb4");
  assert(snapshot.ports.filter(port => port.id.startsWith("fixture-usb-")).every(port =>
    port.transport.code === "usb" && port.transport.ruleId === "usb-hub-port"));
  assert.equal(snapshot.ports.find(port => port.id === "fixture-wireless").network.wireless.negotiatedStandard, "802.11ac");
  await server.scenario({ deviceTopologyWireless: "unknown" });
  assert.equal((await readSnapshot()).ports.find(port => port.id === "fixture-wireless").network.wireless.supportedStandards, null);
});

test("authoritative transport and ordered parent-chain facts survive without name or service inference", async () => {
  await server.scenario({ deviceTopologyGraphicsDock: false, deviceTopologyExternalMachine: false,
    deviceTopologyNumericUsb: false, deviceTopologyWireless: null, deviceTopologyTransport: true });
  try {
    const snapshot = await readSnapshot();
    const port = kind => snapshot.ports.find(port => port.id === `fixture-transport-${kind}`);
    assert.equal(port("code").service, "stornvme");
    assert.match(port("code").displayName, /NVMe USB4/);
    assert.equal(port("code").transport.code, "acpi");
    assert.deepEqual(port("unknown").transport,
      { code: null, reason: "not-queried", ruleId: null, evidence: [] });
    assert.equal(port("pci").transport.code, "pci");
    assert.deepEqual(port("parent").transport, {
      code: "i2c_hid", reason: null, ruleId: "service-hidi2c", evidence: [
        { source: "setupapi-cfgmgr32", deviceId: String.raw`HID\FIXTURE_TRANSPORT_CHILD`, depth: 0,
          enumeratorName: "HID", busTypeGuid: "eeaf37d0-1963-47c4-aa48-72476db7cf49", service: null,
          parentDeviceId: String.raw`ACPI\FIXTURE_TRANSPORT_PARENT` },
        { source: "setupapi-cfgmgr32", deviceId: String.raw`ACPI\FIXTURE_TRANSPORT_PARENT`, depth: 1,
          enumeratorName: "ACPI", busTypeGuid: "d7b46895-001a-4942-891f-a7d46610a843", service: "hidi2c",
          parentDeviceId: null }
      ]
    });
  } finally {
    await server.scenario({ deviceTopologyTransport: false });
  }
});

test("incomplete wire fixtures remain incomplete and fail the production decoder", async () => {
  await server.scenario({ deviceTopologyGraphicsDock: false, deviceTopologyExternalMachine: false,
    deviceTopologyNumericUsb: false, deviceTopologyWireless: "standards", deviceTopologyPorts: null });
  const port = (await readSnapshot()).ports.find(port => port.id === "fixture-wireless");
  await server.scenario({ deviceTopologyWireless: null });
  try {
    for (const field of ["transport", "network.wireless"]) {
      const incomplete = structuredClone(port);
      if (field === "transport") delete incomplete.transport;
      else delete incomplete.network.wireless;
      await server.scenario({ deviceTopologyPorts: [incomplete] });
      const { value } = await server.json("/api/device-topology/state");
      const raw = value.snapshot.ports[0];
      assert.equal(field === "transport" ? Object.hasOwn(raw, "transport") : Object.hasOwn(raw.network, "wireless"), false);
      assert.throws(() => deviceTopologyStateDecoder.decode(value), /transport|wireless/);
    }
  } finally {
    await server.scenario({ deviceTopologyPorts: null });
  }
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
