import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import { startMockServer } from "./mockServerFixture.mjs";
let server;
before(async () => { server = await startMockServer(); });
after(async () => { await server?.close(); });

test("M02 metric empty requests have no default values or observation time", async () => {
  for (const query of ["", "?ids=", "?ids=,%20,"]) {
    const { value } = await server.json("/api/metrics/snapshot" + query);
    assert.deepEqual(value.items, {});
    assert.equal(value.capturedAt, null);
  }
});

test("M02 metric IDs split comma groups, trim, canonicalize and retain unknown placeholders", async () => {
  const { value } = await server.json("/api/metrics/snapshot?ids=%20CPU.usage,%20future.metric%20&ids=cpu.usage,memory.usage");
  assert.deepEqual(Object.keys(value.items).sort(), ["cpu.usage", "future.metric", "memory.usage"]);
  const missing = value.items["future.metric"];
  assert.equal(missing.id, "future.metric");
  assert.equal(missing.numericValue, null);
  assert.equal(missing.unit, "");
  assert.equal(missing.status, 4);
  assert.equal(missing.observedAt ?? null, null);
});

test("M02 unavailable and retained metrics clear every value and observation", async () => {
  await server.scenario({ metricStatuses: { "cpu.usage": 3, "memory.usage": 2 } });
  const { value } = await server.json("/api/metrics/snapshot?ids=cpu.usage,memory.usage");
  assert.equal(value.capturedAt, null);
  for (const [id, status, unit] of [["cpu.usage", 3, "%"], ["memory.usage", 2, "B"]]) {
    const item = value.items[id];
    assert.equal(item.status, status);
    assert.equal(item.unit, unit);
    for (const key of ["numericValue", "integerValue", "bytesValue", "sampleDurationMilliseconds", "percent", "booleanValue", "total", "observedAt"]) assert.equal(item[key] ?? null, null, `${id}.${key}`);
  }
});

test("M02 metric stream uses the same placeholder contract", async () => {
  const frames = await server.frames([{ id: "metrics", path: "/api/metrics/subscribe?ids=unknown.metric&intervalMs=100" }], frames => frames.length > 0);
  assert.equal(frames[0].value.items["unknown.metric"].status, 4);
  assert.equal(frames[0].value.items["unknown.metric"].numericValue, null);
  assert.equal(frames[0].value.capturedAt, null);
});
