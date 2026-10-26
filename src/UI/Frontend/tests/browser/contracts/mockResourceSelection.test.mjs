import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import { startMockServer } from "./mockServerFixture.mjs";
import { decodeResourceBreakdownWireSnapshot } from "../../../src/api/resourceBreakdownWire.ts";
let server;
before(async () => { server = await startMockServer(); });
after(async () => { await server?.close(); });

test("Audit bars and their explicit unavailable replacements publish each metric's r4 facts", async () => {
  await server.scenario({ resourceSoftwareMix: "audit", resourceUnavailableBarIds: [] });
  try {
    const { value } = await server.json("/api/resource-monitor/snapshot?ids=cpu.usage,memory.usage");
    const snapshot = decodeResourceBreakdownWireSnapshot(value.breakdown);
    assert.equal(snapshot.bars.length, 2);
    for (const bar of snapshot.bars) {
      assert.equal(bar.additiveAcrossProcesses, true);
      assert.equal(bar.capturedAt, snapshot.capturedAt);
      assert.equal(bar.processSumValue + bar.residualValue, bar.totalValue);
      assert.ok(bar.software.length >= 10);
    }
    await server.scenario({ resourceUnavailableBarIds: ["cpu.usage"] });
    const { value: unavailable } = await server.json("/api/resource-monitor/snapshot?ids=cpu.usage,memory.usage");
    const wire = unavailable.breakdown;
    const decoded = decodeResourceBreakdownWireSnapshot(wire);
    const cpu = decoded.bars.find(bar => bar.metricId === "cpu.usage");
    assert.deepEqual([cpu.totalValue, cpu.capacityValue, cpu.totalSystemPercent, cpu.processSumValue, cpu.residualValue],
      [null, null, null, null, null]);
    assert.equal(cpu.additiveAcrossProcesses, true);
    assert.equal(cpu.capturedAt, wire.capturedAt);
    assert.equal(cpu.unavailableReason, "hardware-source-unavailable");
    assert.deepEqual(cpu.software, []);
    assert.ok(decoded.bars.find(bar => bar.metricId === "memory.usage").totalValue > 0);
  } finally {
    await server.scenario({ resourceSoftwareMix: "uniform", resourceUnavailableBarIds: [] });
  }
});

test("M01 resource snapshots retain requested unavailable bars", async () => {
  for (const path of ["/api/resource-monitor/snapshot", "/api/resource-breakdown/snapshot"]) {
    for (const [query, expected] of [["", []], ["?ids=", []], ["?ids=gpu.0.usage", ["gpu.0.usage"]], ["?ids=CPU.usage,cpu.usage", ["cpu.usage"]], ["?ids=memory.usage&ids=cpu.usage", ["cpu.usage", "memory.usage"]]]) {
      const { status, value } = await server.json(path + query);
      assert.equal(status, 200);
      const breakdown = value.breakdown ?? value;
      assert.deepEqual(breakdown.bars.map(bar => bar.metricId).sort(), expected, path + query);
      if (query === "?ids=gpu.0.usage") {
        const bar = breakdown.bars[0];
        assert.equal(bar.totalValue, null);
        assert.equal(bar.unavailableReason, "resource-breakdown-total-unavailable");
        assert.deepEqual(bar.software, []);
        assert.deepEqual([bar.processSumValue, bar.residualValue, bar.additiveAcrossProcesses, bar.capturedAt], [null, null, true, null]);
      }
      if (!expected.length) {
        assert.deepEqual(breakdown.softwareCatalog, []);
        assert.equal(breakdown.capturedAt, null);
        assert.equal(value.capturedAt, null);
      }
    }
  }
});

test("M01 resource stream distinguishes empty selection from an unavailable metric", async () => {
  const frames = await server.frames([
    { id: "empty", path: "/api/resource-monitor/subscribe?intervalMs=100" },
    { id: "gpu", path: "/api/resource-monitor/subscribe?ids=gpu.0.usage&intervalMs=100" },
    { id: "memory", path: "/api/resource-monitor/subscribe?ids=memory.usage&intervalMs=100" }
  ], frames => new Set(frames.map(frame => frame.subscriptionId)).size === 3);
  for (const id of ["empty"]) {
    const value = frames.find(frame => frame.subscriptionId === id).value;
    assert.deepEqual(value.breakdown.bars, [], id);
    assert.deepEqual(value.breakdown.softwareCatalog, [], id);
    assert.equal(value.capturedAt, null, id);
  }
  const gpu = frames.find(frame => frame.subscriptionId === "gpu").value.breakdown.bars;
  assert.equal(gpu.length, 1);
  assert.equal(gpu[0].metricId, "gpu.0.usage");
  assert.equal(gpu[0].totalValue, null);
  assert.equal(gpu[0].unavailableReason, "resource-breakdown-total-unavailable");
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

test("M01 unavailable measures retain r4 facts and empty table frames retain selected metrics", async () => {
  const field = "process_all_working_set_actual";
  await server.scenario({ resourceBarMeasureTotalsMissing: [field], resourceTableVariant: "empty" });
  const { value } = await server.json(`/api/resource-monitor/snapshot?measures=${field}`);
  const bar = value.breakdown.bars[0];
  assert.deepEqual([bar.metricId, bar.totalValue, bar.capacityValue, bar.totalSystemPercent, bar.processSumValue, bar.residualValue, bar.additiveAcrossProcesses],
    [field, null, null, null, null, null, false]);
  assert.equal(bar.unavailableReason, "system-total-unavailable");
  assert.equal(typeof bar.capturedAt, "string");
  assert.deepEqual(bar.software, []);
  const frames = await server.frames([{ id: "facts", path: "/api/resource-facts/subscribe?items=cpu.usage&items=memory.usage&intervalMs=100" }], frames => frames.length > 0);
  assert.deepEqual(frames[0].value.metrics.map(metric => [metric.metricId, metric.totalValue, metric.processSumValue, metric.software]),
    [["cpu.usage", 0, 0, []], ["memory.usage", 0, 0, []]]);
  await server.scenario({ resourceBarMeasureTotalsMissing: [], resourceTableVariant: "normal" });
});

test("M01 PFN details retain the published frame time and require a physical selection and residual owner", async () => {
  const physical = { version: 1, source: "pfn", status: "unavailable", reasonCode: "capture-time-limit",
    capturedAt: "2026-10-01T00:00:00.000Z", usedBytes: null, attributedProcessPrivateBytes: null,
    residualBytes: null, unclassifiedBytes: null, parts: [] };
  // 场景只选帧的状态、原因和它自己的采集时间，帧由 mock 按后端形状给出，和这里写出的整帧逐字段相同。
  await server.scenario({ physicalMemoryResidual: "unavailable", physicalMemoryResidualReason: physical.reasonCode,
    physicalMemoryResidualCapturedAt: physical.capturedAt });
  // 保留量按 residualDetails 的指标选：资源条 id，或可加的度量（解析为它的整机指标）；processDetails 里写残差的软件 id 什么也不选。
  for (const [selection, owner, attached] of [
    ["ids=memory.usage", "residualDetails=memory.usage", true],
    ["measures=process_all_private_working_set_actual", "residualDetails=memory.usage", true],
    ["measures=process_all_private_working_set_actual", "residualDetails=process_all_private_working_set_actual", true],
    ["ids=memory.residual.details", "residualDetails=memory.usage", true],
    ["ids=cpu.usage", "residualDetails=memory.usage", false],
    ["measures=process_all_working_set_actual", "residualDetails=memory.usage", false],
    ["ids=memory.usage", "residualDetails=cpu.usage", false],
    ["ids=memory.usage", "residualDetails=", false],
    ["ids=memory.usage", "processDetails=resource-residual:memory.usage", false],
    ["ids=memory.usage", "processDetails=frontend-harness.software", false]
  ]) {
    const frames = await server.frames([{ id: "details", path: `/api/resource-monitor/subscribe?scope=details&${selection}&${owner}&intervalMs=100` }], frames => frames.length > 0);
    const details = frames[0].value;
    assert.deepEqual(details.physicalMemoryResidual, attached ? physical : undefined, selection + owner);
    if (selection.startsWith("measures=process_all_private")) {
      const metric = details.metrics[0];
      assert.equal(metric.metricId, "process_all_private_working_set_actual");
      assert.equal(metric.totalMetricId, "memory.usage");
      assert.notEqual(metric.capturedAt, physical.capturedAt);
      assert(metric.software.some(row => row.softwareId === "resource-residual:memory.usage"));
    }
    if (owner === "processDetails=resource-residual:memory.usage") {
      // 和后端一样：processDetails 非空就照常给出所选指标，但残差的软件 id 是一个不认识的软件，没有任何软件行和分项。
      assert.deepEqual(details.metrics.map(metric => [metric.metricId, metric.software]), [["memory.usage", []]], "the old residual software id is an unknown software");
    }
  }
  await server.scenario({ physicalMemoryResidual: "off", physicalMemoryResidualReason: "capture-time-limit",
    physicalMemoryResidualCapturedAt: "2026-10-26T05:59:50.000Z" });
});

test("M01 residualDetails selects a metric's residual rows like the backend and rejects unsupported metrics", async () => {
  await server.scenario({ resourceSoftwareMix: "audit", resourceUnavailableBarIds: [] });
  try {
    const residualRows = (breakdown, metricId) => {
      const bar = breakdown.bars.find(item => item.metricId === metricId);
      return bar.software.find(row => breakdown.softwareCatalog[row[0]][0] === `resource-residual:${metricId}`)[5];
    };
    for (const path of ["/api/resource-monitor/snapshot", "/api/resource-breakdown/snapshot"]) {
      for (const [selector, selected] of [
        ["residualDetails=memory.usage", ["memory.usage"]],
        ["residualDetails= MEMORY.USAGE ,,memory.usage&residualDetails=cpu.usage", ["cpu.usage", "memory.usage"]],
        ["residualDetails=", []],
        ["", []],
        ["processDetails=resource-residual:memory.usage", []]
      ]) {
        const { status, value } = await server.json(`${path}?ids=cpu.usage,memory.usage&${selector}`);
        assert.equal(status, 200, path + selector);
        const breakdown = value.breakdown ?? value;
        for (const metricId of ["cpu.usage", "memory.usage"]) {
          assert.equal(residualRows(breakdown, metricId).length > 0, selected.includes(metricId), `${path}?${selector}: ${metricId}`);
        }
      }
      for (const invalid of ["unknown.metric", "process_all_working_set_actual", "memory.residual.details", "memory.usage,unknown.metric"]) {
        const { status, value } = await server.json(`${path}?ids=memory.usage&residualDetails=${invalid}`);
        assert.equal(status, 400, path + invalid);
        assert.match(value.error, /Resource residual details metric/);
      }
    }
    const { status, value } = await server.json("/api/subscriptions/stream", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ version: 1, subscriptions: [{ id: "valid", path: "/api/resource-monitor/subscribe?scope=bars&ids=cpu.usage" },
        { id: "invalid", path: "/api/resource-monitor/subscribe?scope=bars&ids=memory.usage&residualDetails=unknown.metric" }] }) });
    assert.equal(status, 400);
    assert.equal(value.error, "One or more subscription queries are invalid.");
  } finally {
    await server.scenario({ resourceSoftwareMix: "uniform", resourceUnavailableBarIds: [] });
  }
});
