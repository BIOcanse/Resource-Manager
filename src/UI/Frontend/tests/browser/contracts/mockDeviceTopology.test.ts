import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import { startMockServer } from "./mockServerFixture.ts";
import { deviceTopologyStateDecoder } from "../../../src/features/deviceTopology/deviceTopologyStateDecoder.ts";
import { networkPort } from "../deviceTopologyConnectionProbe.ts";
import type { MockFrame, MockSubscription } from "./mockServerFixture.ts";
import type {
  DeviceTopologySnapshotState, DeviceTopologySnapshot, DeviceTopologyPort, DeviceTopologyStorageDevice,
  DeviceTopologyStoragePartition, DeviceTopologyStorageVolume, DeviceTopologyInformationSnapshot
} from "../../../src/types.ts";
type StorageWire = Omit<DeviceTopologyStorageDevice, "partitions"> & {
  partitions: (Omit<DeviceTopologyStoragePartition, "volumes"> & {
    volumes: (DeviceTopologyStorageVolume & { freeBytes: string | null })[];
  })[];
};
type PortWire = Omit<DeviceTopologyPort, "storage"> & { storage?: StorageWire | null };
type TopologyStateWire = Omit<DeviceTopologySnapshotState, "snapshot" | "lastSuccessAt" | "lastAttemptAt"> & {
  snapshot: Omit<DeviceTopologySnapshot, "ports"> & { ports: PortWire[] };
  lastSuccessAt: string;
  lastAttemptAt: string;
};
interface StreamValues {
  state: TopologyStateWire;
  fields: DeviceTopologyInformationSnapshot;
  catalog: { catalogRevision: string };
  free: DeviceTopologyInformationSnapshot;
}

let server: Awaited<ReturnType<typeof startMockServer>>;
before(async () => { server = await startMockServer(); });
after(async () => { await server?.close(); });

async function readSnapshot() {
  const { status, value } = await server.json<TopologyStateWire>("/api/device-topology/state");
  assert.equal(status, 200);
  const decoded = deviceTopologyStateDecoder.decode(value).snapshot!;
  for (const field of ["machineIdentity", "gpuInformation", "externalMachines"] as const)
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
  assert.equal(snapshot.gpuInformation![0].attachmentKind, "graphics_dock");
  assert.equal(snapshot.externalMachines![0].model, null);
  assert.deepEqual(snapshot.externalMachines![0].components, [{ component: "soc", model: null }]);
  assert.equal(snapshot.ports.find(port => port.id === "fixture-peer-link")!.transport.code, "usb4");
  assert(snapshot.ports.filter(port => port.id.startsWith("fixture-usb-")).every(port =>
    port.transport.code === "usb" && port.transport.ruleId === "usb-hub-port"));
  assert.equal(snapshot.ports.find(port => port.id === "fixture-wireless")!.network!.wireless!.negotiatedStandard, "802.11ac");
  await server.scenario({ deviceTopologyWireless: "unknown" });
  assert.equal((await readSnapshot()).ports.find(port => port.id === "fixture-wireless")!.network!.wireless!.supportedStandards, null);
});

test("authoritative transport and ordered parent-chain facts survive without name or service inference", async () => {
  await server.scenario({ deviceTopologyGraphicsDock: false, deviceTopologyExternalMachine: false,
    deviceTopologyNumericUsb: false, deviceTopologyWireless: null, deviceTopologyTransport: true });
  try {
    const snapshot = await readSnapshot();
    const port = (kind: string) => snapshot.ports.find(port => port.id === `fixture-transport-${kind}`)!;
    assert.equal(port("code").service, "stornvme");
    assert.match(port("code").displayName!, /NVMe USB4/);
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
  const port = (await readSnapshot()).ports.find(port => port.id === "fixture-wireless")!;
  await server.scenario({ deviceTopologyWireless: null });
  try {
    for (const field of ["transport", "network.wireless"]) {
      const incomplete: Partial<DeviceTopologyPort> = structuredClone(port);
      if (field === "transport") delete incomplete.transport;
      else delete incomplete.network!.wireless;
      await server.scenario({ deviceTopologyPorts: [incomplete] });
      const { value } = await server.json<{ snapshot: { ports: Partial<DeviceTopologyPort>[] } }>("/api/device-topology/state");
      const raw = value.snapshot.ports[0];
      assert.equal(field === "transport" ? Object.hasOwn(raw, "transport") : Object.hasOwn(raw.network!, "wireless"), false);
      assert.throws(() => deviceTopologyStateDecoder.decode(value), /transport|wireless/);
    }
  } finally {
    await server.scenario({ deviceTopologyPorts: null });
  }
});

test("network connection journey supplies explicit nullable wireless facts", async () => {
  await server.scenario({ deviceTopologyGraphicsDock: false, deviceTopologyExternalMachine: false,
    deviceTopologyNumericUsb: false, deviceTopologyWireless: null });
  for (const state of ["connected", "disconnected", "unknown", "not-reported"] as const) {
    await server.scenario({ deviceTopologyPorts: [networkPort(state)] });
    const snapshot = await readSnapshot();
    assert.equal(snapshot.ports[0].network!.connectionState, state);
    assert.equal(snapshot.ports[0].network!.wireless, null);
  }
});

// 一条统一订阅通道，收下全部帧；waitFor 等到条件成立（有界），quiet 观察一段时间里没有新帧。
async function openStream(subscriptions: MockSubscription[]) {
  const controller = new AbortController();
  const response = await fetch(`${server.baseUrl}/api/subscriptions/stream`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ version: 1, subscriptions }), signal: controller.signal
  });
  assert.equal(response.status, 200);
  const reader = response.body!.getReader();
  const decoder = new TextDecoder();
  const frames: MockFrame<StreamValues[keyof StreamValues]>[] = [];
  let pending = "";
  const pump = (async () => {
    for (;;) {
      const { done, value } = await reader.read().catch((): { done: true; value?: Uint8Array } => ({ done: true }));
      if (done) return;
      pending += decoder.decode(value, { stream: true });
      const lines = pending.split("\n");
      pending = lines.pop()!;
      for (const line of lines) if (line.trim()) frames.push(JSON.parse(line) as MockFrame<StreamValues[keyof StreamValues]>);
    }
  })();
  const of = <K extends keyof StreamValues>(id: K): StreamValues[K][] =>
    frames.filter(frame => frame.subscriptionId === id).map(frame => frame.value as StreamValues[K]);
  return {
    of,
    async waitFor(description: string, predicate: () => boolean, timeoutMs = 5_000) {
      const deadline = Date.now() + timeoutMs;
      while (!predicate()) {
        if (Date.now() > deadline) assert.fail(`timed out waiting for ${description}: ${JSON.stringify(frames.map(frame => frame.subscriptionId))}`);
        await new Promise(resolve => setTimeout(resolve, 20));
      }
    },
    async quiet(id: keyof StreamValues, durationMs: number) {
      const count = of(id).length;
      await new Promise(resolve => setTimeout(resolve, durationMs));
      assert.equal(of(id).length, count, `${id} sent a frame without a content change`);
    },
    async close() { controller.abort(); await reader.cancel().catch(() => {}); await pump; }
  };
}

test("topology structure and the metric catalog push once, then only on change; sampled fields push every sample", async () => {
  await server.json("/__test/scenario/reset", { method: "POST" });
  await server.scenario({ deviceTopologyStorage: true });
  const stream = await openStream([
    { id: "state", path: "/api/device-topology/state/subscribe?intervalMs=100" },
    { id: "fields", path: "/api/device-topology/fields/subscribe?intervalMs=100" },
    { id: "catalog", path: "/api/metrics/fields/catalog/subscribe?intervalMs=100" }
  ]);
  try {
    await stream.waitFor("first frames", () => stream.of("state").length === 1 && stream.of("catalog").length === 1
      && stream.of("fields").length >= 3);
    await stream.quiet("state", 500);
    assert.equal(stream.of("catalog").length, 1, "an unchanged catalog was resent");

    // 结构帧：卷只带 EventDriven 的挂载路径，快照里卷的 freeBytes 为 null，前端解码不读它；可用空间只在字段帧里。
    const [first] = stream.of("state");
    const decoded = deviceTopologyStateDecoder.decode(first);
    assert.deepEqual(first.informationFields.devices.map(device => device.fields.map(field => field.definition.name)),
      [["volume_all_mount_paths_reported"]]);
    const wireVolume = first.snapshot.ports.find(port => port.id === "fixture-usb-storage-volumes")!.storage!.partitions[0].volumes[0];
    assert.equal(wireVolume.freeBytes, null);
    const volume = decoded.snapshot!.ports.find(port => port.id === "fixture-usb-storage-volumes")!.storage!.partitions[0].volumes[0];
    assert.equal("freeBytes" in volume, false);
    const fields = stream.of("fields");
    assert.deepEqual(fields.at(-1)!.devices[0].fields.map(field => [field.definition.name, field.value]),
      [["volume_all_mount_paths_reported", ["F:\\"]], ["volume_all_free_bytes_actual", "250092956672"]]);
    assert.ok(Date.parse(fields.at(-1)!.observedAt!) > Date.parse(fields[0].observedAt!), "each sample has its own observation time");

    // 只改采样值：字段帧带新值，结构不推。
    await server.scenario({ deviceTopologyVolumeFreeBytes: "200000000000" });
    await stream.waitFor("the sampled free space", () => stream.of("fields").at(-1)!.devices[0].fields[1].value === "200000000000");
    await stream.quiet("state", 300);

    // 结构变化推一帧；lastSuccessAt 是提交这次变化的采集时间。
    await server.scenario({ deviceTopologyBiosVersion: "FIXTURE.1" });
    await stream.waitFor("the structural change", () => stream.of("state").length === 2);
    const changed = stream.of("state")[1];
    assert.equal(changed.snapshot.system.biosVersion, "FIXTURE.1");
    assert.ok(Date.parse(changed.lastSuccessAt) > Date.parse(first.lastSuccessAt));
    assert.equal(changed.lastSuccessAt, changed.snapshot.capturedAt);
    await stream.quiet("state", 300);

    // 健康变化照推：失败推一帧（快照和 lastSuccessAt 沿用上次成功的提交），失败不变不再推，恢复推一帧。
    await server.scenario({ deviceTopologyFailure: true });
    await stream.waitFor("the failure", () => stream.of("state").length === 3);
    const failed = stream.of("state")[2];
    assert.equal(failed.state, "failed");
    assert.equal(failed.lastSuccessAt, changed.lastSuccessAt);
    assert.deepEqual(failed.snapshot, changed.snapshot);
    assert.ok(Date.parse(failed.lastAttemptAt) > Date.parse(changed.lastAttemptAt));
    await stream.quiet("state", 300);
    await server.scenario({ deviceTopologyFailure: false });
    await stream.waitFor("the recovery", () => stream.of("state").length === 4);
    const recovered = stream.of("state")[3];
    assert.equal(recovered.state, "ready");
    assert.equal(recovered.snapshot.capturedAt, changed.snapshot.capturedAt, "unchanged structure keeps its captured snapshot");
    assert.ok(Date.parse(recovered.lastSuccessAt) > Date.parse(failed.lastSuccessAt));

    // 目录：组件安装信息也是内容，换修订号并推一帧。
    const firstRevision = stream.of("catalog")[0].catalogRevision;
    await server.scenario({ metricCatalogComponent: "installed" });
    await stream.waitFor("the catalog change", () => stream.of("catalog").length === 2);
    assert.notEqual(stream.of("catalog")[1].catalogRevision, firstRevision);
    await stream.quiet("catalog", 300);
  } finally {
    await stream.close();
    await server.json("/__test/scenario/reset", { method: "POST" });
  }
});

test("a field selection pushes only the selected fields, once and then only on change", async () => {
  await server.json("/__test/scenario/reset", { method: "POST" });
  await server.scenario({ deviceTopologyStorage: true });
  const { value: state } = await server.json<TopologyStateWire>("/api/device-topology/state");
  const identity = state.informationFields.devices[0].deviceIdentity;
  const stream = await openStream([{ id: "free", path: "/api/device-topology/fields/subscribe?intervalMs=100",
    fields: [{ deviceIdentity: identity, fieldName: "volume_all_free_bytes_actual" }] }]);
  try {
    await stream.waitFor("the first selected frame", () => stream.of("free").length === 1);
    const [first] = stream.of("free");
    assert.deepEqual(first.devices.map(device => [device.deviceIdentity, device.fields.map(field => [field.definition.name, field.value])]),
      [[identity, [["volume_all_free_bytes_actual", "250092956672"]]]]);
    await stream.quiet("free", 400);
    await server.scenario({ deviceTopologyVolumeFreeBytes: "200000000000" });
    await stream.waitFor("the changed selected field", () => stream.of("free").length === 2);
    assert.equal(stream.of("free")[1].devices[0].fields[0].value, "200000000000");
    await stream.quiet("free", 300);
  } finally {
    await stream.close();
    await server.json("/__test/scenario/reset", { method: "POST" });
  }

  // 空选择、重复字段和未声明的字段都让整个请求 400。
  for (const fields of [[], [{ deviceIdentity: identity, fieldName: "volume_all_free_bytes_actual" },
    { deviceIdentity: identity, fieldName: "volume_all_free_bytes_actual" }], [{ deviceIdentity: identity, fieldName: "unknown_field" }]]) {
    const response = await fetch(`${server.baseUrl}/api/subscriptions/stream`, { method: "POST",
      headers: { "Content-Type": "application/json" }, body: JSON.stringify({ version: 1,
        subscriptions: [{ id: "free", path: "/api/device-topology/fields/subscribe?intervalMs=100", fields }] }) });
    assert.equal(response.status, 400, JSON.stringify(fields));
    await response.body?.cancel();
  }
});
