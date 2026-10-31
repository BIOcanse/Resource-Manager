import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import { startMockServer } from "./mockServerFixture.ts";
import type { MetricValue } from "../../../src/types.ts";
interface FieldDeviceWire {
  deviceIdentity: string;
  device: { kind: string; address: null; displayName: string; ordinal: number };
  fields: (MetricValue & { name: string })[];
}
let server: Awaited<ReturnType<typeof startMockServer>>;
const f1 = "disk_all_temperature_celsius";
const f2 = "storage_all_nvme_composite_temperature_celsius";
const devices: FieldDeviceWire[] = ["disk|A", "disk|B"].map((deviceIdentity, ordinal) => ({
  deviceIdentity, device: { kind: "storage", address: null, displayName: deviceIdentity, ordinal },
  fields: [f1, f2].map((name, index) => ({ name, unit: "°C", valueKind: "float64", status: 1, numericValue: 30 + ordinal * 10 + index, observedAt: "2026-10-25T00:00:00Z", sourceId: "fixture" }))
}));
before(async () => { server = await startMockServer(); await server.scenario({ metricFieldDevices: devices }); });
after(async () => { await server?.close(); });
const selection = (query: string[][]) => new URLSearchParams(query).toString();
const summary = (value: FieldDeviceWire[]) => value.map(device => [device.deviceIdentity, device.fields.map(field => field.name).sort()]);

test("M04 field snapshots project each device/field group and merge repeated devices", async () => {
  for (const [groups, expected] of [
    [[["device", "disk|A"], ["fields", f1]], [["disk|A", [f1]]]],
    [[["device", "disk|A"], ["fields", f1], ["device", "disk|B"], ["fields", f2]], [["disk|A", [f1]], ["disk|B", [f2]]]],
    [[["device", "disk|A"], ["fields", f1], ["device", "disk|A"], ["fields", f2]], [["disk|A", [f1, f2].sort()]]],
    [[["fields", f1], ["fields", f2]], [["disk|A", [f1, f2].sort()], ["disk|B", [f1, f2].sort()]]]
  ] satisfies [string[][], [string, string[]][]][]) {
    const { status, value } = await server.json<FieldDeviceWire[]>("/api/metrics/fields/snapshot?" + selection(groups));
    assert.equal(status, 200);
    assert.deepEqual(summary(value), expected);
    assert.equal(value[0].fields[0].numericValue, 30);
  }
});

test("M04 invalid field groups return 400 instead of ordinary metric snapshots", async () => {
  for (const groups of [[], [["device", "disk|A"]], [["device", ""]], [["device", "missing"], ["fields", f1]], [["fields", "unknown_field"]], [["device", "disk|A"], ["fields", "unknown_field"]], [["device", "disk|A"], ["device", "disk|B"], ["fields", f1]]]) {
    const response = await fetch(`${server.baseUrl}/api/metrics/fields/snapshot?${selection(groups)}`);
    assert.equal(response.status, 400, JSON.stringify(groups));
  }
});

test("M04 globally known fields without bindings return an empty field snapshot", async () => {
  const { status, value } = await server.json<FieldDeviceWire[]>("/api/metrics/fields/snapshot?fields=fan_all_speed_rpm");
  assert.equal(status, 200);
  assert.deepEqual(value, []);
});

test("M04 field subscriptions apply device scope and all field groups", async () => {
  const groups = [["device", "disk|A"], ["fields", f1], ["device", "disk|B"], ["fields", f2]];
  const frames = await server.frames<FieldDeviceWire[]>([{ id: "fields", path: `/api/metrics/subscribe?${selection(groups)}&intervalMs=100` }], frames => frames.length > 0);
  assert.deepEqual(summary(frames[0].value), [["disk|A", [f1]], ["disk|B", [f2]]]);
});

test("M04 device-only subscription is an invalid query, not ordinary metrics", async () => {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 5_000);
  try {
    const response = await fetch(`${server.baseUrl}/api/subscriptions/stream`, {
      method: "POST", headers: { "Content-Type": "application/json" }, signal: controller.signal,
      body: JSON.stringify({ version: 1, subscriptions: [
        { id: "invalid-fields", path: "/api/metrics/subscribe?device=disk%7CA&intervalMs=100" },
        { id: "valid", path: `/api/metrics/subscribe?fields=${f1}&intervalMs=100` }
      ] })
    });
    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), { error: "One or more subscription queries are invalid." });
  } finally { clearTimeout(timer); controller.abort(); }
});

test("M04 selected non-current fields clear stale values and observation times", async () => {
  const unavailable = structuredClone(devices);
  unavailable[0].fields[0] = { ...unavailable[0].fields[0], status: 3, integerValue: "999", sampleDurationMilliseconds: 1000 };
  await server.scenario({ metricFieldDevices: unavailable });
  const { value } = await server.json<FieldDeviceWire[]>("/api/metrics/fields/snapshot?" + selection([["device", "disk|A"], ["fields", f1]]));
  assert.equal(value[0].fields[0].status, 3);
  for (const key of ["numericValue", "integerValue", "bytesValue", "booleanValue", "sampleDurationMilliseconds", "observedAt"] as const) assert.equal(value[0].fields[0][key] ?? null, null, key);
});
