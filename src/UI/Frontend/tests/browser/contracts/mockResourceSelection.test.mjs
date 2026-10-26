import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import { startMockServer } from "./mockServerFixture.mjs";
let server;
before(async () => { server = await startMockServer(); });
after(async () => { await server?.close(); });

test("M01 resource snapshots return only requested, available bars", async () => {
  for (const path of ["/api/resource-monitor/snapshot", "/api/resource-breakdown/snapshot"]) {
    for (const [query, expected] of [["", []], ["?ids=", []], ["?ids=gpu.0.usage", []], ["?ids=CPU.usage,cpu.usage", ["cpu.usage"]], ["?ids=memory.usage&ids=cpu.usage", ["cpu.usage", "memory.usage"]]]) {
      const { status, value } = await server.json(path + query);
      assert.equal(status, 200);
      const breakdown = value.breakdown ?? value;
      assert.deepEqual(breakdown.bars.map(bar => bar.metricId).sort(), expected, path + query);
      if (!expected.length) {
        assert.deepEqual(breakdown.softwareCatalog, []);
        assert.equal(breakdown.capturedAt, null);
        assert.equal(value.capturedAt, null);
      }
    }
  }
});

test("M01 resource stream keeps empty and unavailable requests empty", async () => {
  const frames = await server.frames([
    { id: "empty", path: "/api/resource-monitor/subscribe?intervalMs=100" },
    { id: "gpu", path: "/api/resource-monitor/subscribe?ids=gpu.0.usage&intervalMs=100" },
    { id: "memory", path: "/api/resource-monitor/subscribe?ids=memory.usage&intervalMs=100" }
  ], frames => new Set(frames.map(frame => frame.subscriptionId)).size === 3);
  for (const id of ["empty", "gpu"]) {
    const value = frames.find(frame => frame.subscriptionId === id).value;
    assert.deepEqual(value.breakdown.bars, [], id);
    assert.deepEqual(value.breakdown.softwareCatalog, [], id);
    assert.equal(value.capturedAt, null, id);
  }
  assert.deepEqual(frames.find(frame => frame.subscriptionId === "memory").value.breakdown.bars.map(bar => bar.metricId), ["memory.usage"]);
});

test("M01 explicit process measures do not add default CPU or memory bars", async () => {
  const { value } = await server.json("/api/resource-monitor/snapshot?measures=process_all_private_commit_actual");
  assert.deepEqual(value.breakdown.bars.map(bar => bar.metricId), ["process_all_private_commit_actual"]);
});

test("M01 GPU pressure bars honor selection and separate process count from requested details", async () => {
  await server.scenario({ gpuPressureFixture: true });
  for (const detail of [false, true]) {
    const { value } = await server.json(`/api/resource-monitor/snapshot?ids=gpu.0.usage,gpu.0.vram${detail ? "&processDetails=frontend-harness.software" : ""}`);
    assert.deepEqual(value.breakdown.bars.map(bar => bar.metricId), ["gpu.0.usage", "gpu.0.vram"]);
    for (const bar of value.breakdown.bars) {
      assert(bar.software[0][3] > 0, "Process count is present without process details");
      assert.equal(bar.software[0][5].length > 0, detail);
    }
  }
});

test("M01 comma-group measures select each canonical process field once in snapshots and streams", async () => {
  const fields = ["process_all_working_set_actual", "process_all_private_commit_actual"];
  const query = new URLSearchParams([
    ["measures", ` ${fields[0].toUpperCase()}, ${fields[1]} `],
    ["measures", fields[0]], ["intervalMs", "100"]
  ]).toString();
  const { status, value } = await server.json(`/api/resource-monitor/snapshot?${query}`);
  assert.equal(status, 200);
  assert.deepEqual(value.breakdown.bars.map(bar => bar.metricId).sort(), fields.toSorted());
  const frames = await server.frames([{ id: "measures", path: `/api/resource-monitor/subscribe?${query}` }], frames => frames.length > 0);
  assert.deepEqual(frames[0].value.breakdown.bars.map(bar => bar.metricId).sort(), fields.toSorted());
});

test("M01 ids-only process field selections produce bars in both snapshots and streams", async () => {
  const field = "process_all_private_working_set_actual";
  const query = new URLSearchParams({ ids: ` ${field.toUpperCase()},${field} `, intervalMs: "100" }).toString();
  for (const path of ["/api/resource-monitor/snapshot", "/api/resource-breakdown/snapshot"]) {
    const { status, value } = await server.json(`${path}?${query}`);
    assert.equal(status, 200);
    const breakdown = value.breakdown ?? value;
    assert.deepEqual(breakdown.bars.map(bar => bar.metricId), [field]);
  }
  const frames = await server.frames([{ id: "ids", path: `/api/resource-monitor/subscribe?${query}` }], frames => frames.length > 0);
  assert.deepEqual(frames[0].value.breakdown.bars.map(bar => bar.metricId), [field]);
});

test("M01 process detail software IDs split comma groups, trim and compare without case", async () => {
  await server.scenario({ resourceSegmentCount: 2 });
  const query = new URLSearchParams([
    ["ids", "cpu.usage"],
    ["processDetails", " FRONTEND-HARNESS.SOFTWARE , FRONTEND-HARNESS.SOFTWARE.1 "],
    ["processDetails", "frontend-harness.software"], ["intervalMs", "100"]
  ]).toString();
  for (const path of ["/api/resource-monitor/snapshot", "/api/resource-breakdown/snapshot"]) {
    const { value } = await server.json(`${path}?${query}`);
    const breakdown = value.breakdown ?? value;
    assert.deepEqual(breakdown.bars[0].software.map(row => row[5].length), [1, 1], path);
  }
  const frames = await server.frames([
    { id: "bars", path: `/api/resource-monitor/subscribe?scope=bars&${query}` },
    { id: "details", path: `/api/resource-monitor/subscribe?scope=details&${query}` }
  ], frames => new Set(frames.map(frame => frame.subscriptionId)).size === 2);
  assert.deepEqual(frames.find(frame => frame.subscriptionId === "bars").value.breakdown.bars[0].software.map(row => row[5].length), [1, 1]);
  assert.deepEqual(frames.find(frame => frame.subscriptionId === "details").value.metrics[0].software.map(row => row.processes.length), [1, 1]);
});
