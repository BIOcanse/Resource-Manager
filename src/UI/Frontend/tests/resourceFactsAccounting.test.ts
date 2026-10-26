import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { decodeResourceFacts, projectResourceFacts } from "../src/features/resourceTable/resourceFacts.ts";
import { processFieldCatalogDecoder } from "../src/data/resourceMonitor/processFieldCatalog.ts";
import { factsMetricKeys } from "./resourceFactsWireFixture.ts";
import { gpuEngineField, gpuResidentField, staticProcessFieldCatalog } from "./processFieldCatalogFixture.ts";

/*
 * 资源事实的核算合同（BACKEND-RF-CLOSURE）：可加口径的整机总量 = 本帧发布的进程之和 + 带符号的残差，
 * 表格按软件或按进程/归属行相加都等于总量；不可加口径没有残差；没有总量的指标写原因、不发行。
 * 合同在后端 ResourceFactsSnapshot.Balance（metric-source round 4）里成立，前端只验证、不补算。
 *
 * authorityFrame 按 Balance 的写法生成 round-4 形状的帧：PID ≤ 0 的行和没有进程的软件不进软件分项
 * （原来 GPU0 的软件级“未归属”显存因此并入残差），差额记在 resource-residual:<totalMetricId> 下的一个叶子。
 */
type Row = { softwareId: string; processes: { pid: number; value: number }[] };
type Input = { metricId: string; unit: string; total: number | null; capacity: number | null; additive: boolean; rows: Row[];
  unavailableReason?: string; windowMilliseconds?: number };

function authorityFrame(inputs: Input[]) {
  const capturedAt = "2026-10-26T00:00:00Z";
  const software = new Map<string, { softwareId: string; name: string; kind: string; attributionKind: string }>();
  const processes: { processId: number; processStartKey: string | null; name: string; executablePath: null; userName: null; architecture: null; attributionKind: string }[] = [];
  const attributions: { processIndex: number; softwareId: string; baseScore: number }[] = [];
  const percent = (value: number, capacity: number | null) => capacity !== null && capacity > 0 ? value * 100 / capacity : 0;
  const index = (pid: number, kind: string) => {
    const found = processes.findIndex(process => process.processId === pid && process.attributionKind === kind);
    if (found >= 0) return found;
    return processes.push({ processId: pid, processStartKey: pid > 0 ? String(132000000000000000 + pid) : null, name: pid > 0 ? `p${pid}.exe` : "",
      executablePath: null, userName: null, architecture: null, attributionKind: kind }) - 1;
  };
  const metrics = inputs.map(input => {
    // GPU memory without a dedicated total is published unavailable with no rows; other metrics keep their rows (Balance).
    if (input.total === null && /^gpu\.\d+\.vram$/.test(input.metricId)) {
      return { metricId: input.metricId, unit: input.unit, totalValue: null, capacityValue: null, totalSystemPercent: null, sharedValue: null, software: [],
        processFieldName: null, ...factsMetricKeys({ totalMetricId: input.metricId, processSumValue: null, residualValue: null,
          additiveAcrossProcesses: input.additive, capturedAt, unavailableReason: input.unavailableReason ?? "system-total-unavailable" }) };
    }
    const values = input.rows.map(row => ({ ...row, processes: row.processes.filter(process => process.pid > 0) }))
      .filter(row => row.processes.length > 0).map(row => {
        software.set(row.softwareId, { softwareId: row.softwareId, name: row.softwareId, kind: "other", attributionKind: "other" });
        const sum = row.processes.reduce((total, process) => total + process.value, 0);
        return { softwareId: row.softwareId, value: sum, systemPercent: percent(sum, input.capacity), processCount: row.processes.length, baseScore: 0, sharedValue: null,
          processes: row.processes.map(process => {
            const processIndex = index(process.pid, "process");
            if (!attributions.some(edge => edge.processIndex === processIndex && edge.softwareId === row.softwareId))
              attributions.push({ processIndex, softwareId: row.softwareId, baseScore: 0 });
            return { processIndex, value: process.value, systemPercent: percent(process.value, input.capacity),
              softwarePercent: sum > 0 ? process.value * 100 / sum : 0, baseScore: 0, sharedValue: null };
          }) };
      });
    const sum = values.reduce((total, row) => total + row.processes.reduce((inner, process) => inner + process.value, 0), 0);
    const residual = input.additive && input.total !== null ? input.total - sum : null;
    if (residual !== null && residual !== 0) {
      const id = `resource-residual:${input.metricId}`;
      software.set(id, { softwareId: id, name: "", kind: "windows_system", attributionKind: "system-residual" });
      const processIndex = index(-1, "system-residual");
      attributions.push({ processIndex, softwareId: id, baseScore: 0 });
      values.push({ softwareId: id, value: residual, systemPercent: percent(residual, input.capacity), processCount: 0, baseScore: 0, sharedValue: null,
        processes: [{ processIndex, value: residual, systemPercent: percent(residual, input.capacity), softwarePercent: 100, baseScore: 0, sharedValue: null }] });
    }
    return { metricId: input.metricId, unit: input.unit, totalValue: input.total, capacityValue: input.capacity,
      totalSystemPercent: input.total === null ? null : percent(input.total, input.capacity), sharedValue: null, software: values, processFieldName: null,
      ...factsMetricKeys({ totalMetricId: input.metricId, processSumValue: sum, residualValue: residual, additiveAcrossProcesses: input.additive, capturedAt,
        windowMilliseconds: input.windowMilliseconds ?? null, unavailableReason: input.total === null ? "system-total-unavailable" : null }) };
  });
  return { version: 2, capturedAt, software: [...software.values()], processes, attributions, metrics };
}

const columnOf = (id: string) => ({ "cpu.usage": "cpu", "memory.usage": "memory", "disk.io": "disk", "network.traffic": "network" }[id] ?? id);
const frame = decodeResourceFacts(authorityFrame([
  { metricId: "cpu.usage", unit: "%", total: 59.67, capacity: 100, additive: true,
    rows: [{ softwareId: "a", processes: [{ pid: 10, value: 17.4 }, { pid: 11, value: 6.9 }] }, { softwareId: "unattributed", processes: [{ pid: 12, value: 2.2 }] }] },
  { metricId: "memory.usage", unit: "B", total: 29574270976, capacity: 34359738368, additive: true,
    rows: [{ softwareId: "a", processes: [{ pid: 10, value: 10428006400 }] }, { softwareId: "b", processes: [{ pid: 13, value: 2366824448 }] }] },
  // The pre-round-4 capture published GPU0 software-level "unattributed" VRAM without processes; Balance folds it into the residual.
  { metricId: "gpu.0.vram", unit: "B", total: 2037866496, capacity: 8589934592, additive: true,
    rows: [{ softwareId: "a", processes: [{ pid: 10, value: 722987690.6666666 }] }, { softwareId: "b", processes: [{ pid: 13, value: 243974144 }] },
      { softwareId: "unattributed", processes: [{ pid: -1, value: 271908864 }] }] },
  { metricId: "gpu.1.vram", unit: "B", total: null, capacity: null, additive: true, rows: [{ softwareId: "a", processes: [{ pid: 10, value: 16732160 }] }],
    unavailableReason: "gpu-dedicated-memory-total-unavailable" },
  { metricId: "network.traffic", unit: "B/s", total: 302454.67, capacity: 302454.67, additive: true, windowMilliseconds: 2000,
    rows: [{ softwareId: "b", processes: [{ pid: 13, value: 278997.76 }] }] },
  { metricId: "process_all_working_set_actual", unit: "B", total: 29574270976, capacity: 34359738368, additive: false,
    rows: [{ softwareId: "a", processes: [{ pid: 10, value: 20000000000 }] }, { softwareId: "b", processes: [{ pid: 13, value: 15000000000 }] }] }
])).facts!;
const columns = ["cpu", "memory", "gpu.0.vram", "gpu.1.vram", "network"].map(id => ({ id, visible: true }))
  .concat([{ id: "memory", visible: true, source: "process_all_working_set_actual" } as never]);

function assertCloses(total: number, values: number[], detail: string) {
  const sum = values.reduce((left, right) => left + right, 0);
  const exact = Number.isSafeInteger(total) && values.every(Number.isSafeInteger);
  const roundoff = exact ? 0 : Number.EPSILON * Math.max(1, Math.abs(total), Math.abs(sum)) * Math.max(1, values.length) * 2;
  assert.ok(Math.abs(total - sum) <= roundoff, `BACKEND-RF-CLOSURE: ${detail}; total=${total}; rows+residual=${sum}`);
}

for (const metric of frame.metrics.filter(metric => metric.additiveAcrossProcesses && metric.totalValue !== null)) {
  test(`${metric.metricId}: BACKEND-RF-CLOSURE holds in the frame and in both table views`, () => {
    assertCloses(metric.totalValue!, [metric.processSumValue!, metric.residualValue!], `${metric.metricId} facts`);
    assertCloses(metric.totalValue!, metric.software.map(item => item.value), `${metric.metricId} software`);
    for (const mode of ["software", "process"] as const) {
      const id = columnOf(metric.metricId);
      const projected = projectResourceFacts(frame, [{ id, visible: true }], mode, "name", "asc");
      assert.equal(projected.rows[0].values[id].value, metric.totalValue);
      assertCloses(metric.totalValue!, projected.rows.filter(row => mode === "software" ? row.kind === "software" : row.kind === "process" || row.kind === "attribution")
        .map(row => row.values[id]?.value ?? 0), `${metric.metricId}/${mode}`);
      assert.ok(projected.rows.every(row => row.processId !== -1 && !row.processIds?.includes(-1)));
    }
  });
}

test("GPU0 unattributed VRAM without a process stays in the table as the residual (formerly dropped)", () => {
  const metric = frame.metrics.find(metric => metric.metricId === "gpu.0.vram")!;
  assert.equal(metric.residualValue, 2037866496 - (722987690.6666666 + 243974144), "residual = total − published process sum");
  assert.ok(metric.residualValue! >= 271908864, "The process-less unattributed VRAM is part of the residual");
  const leaf = projectResourceFacts(frame, [{ id: "gpu.0.vram", visible: true }], "process", "name", "asc").rows
    .find(row => row.kind === "attribution" && row.softwareId === "resource-residual:gpu.0.vram")!;
  assert.equal(leaf.values["gpu.0.vram"].value, metric.residualValue);
  assert.equal(leaf.processId, null);
});

test("A metric without a total publishes its reason and no rows (shared-only integrated GPU)", () => {
  const metric = frame.metrics.find(metric => metric.metricId === "gpu.1.vram")!;
  assert.deepEqual([metric.totalValue, metric.processSumValue, metric.residualValue, metric.unavailableReason, metric.software],
    [null, null, null, "gpu-dedicated-memory-total-unavailable", []]);
  const projected = projectResourceFacts(frame, columns, "process", "name", "asc");
  assert.equal(projected.rows[0].values["gpu.1.vram"].unavailableReason, "gpu-dedicated-memory-total-unavailable");
  assert.ok(projected.rows.slice(1).every(row => row.values["gpu.1.vram"] === undefined));
});

test("A non-additive metric has no residual value and no residual row", () => {
  const metric = frame.metrics.find(metric => metric.metricId === "process_all_working_set_actual")!;
  assert.equal(metric.additiveAcrossProcesses, false);
  assert.equal(metric.residualValue, null);
  assert.ok(!metric.software.some(item => item.softwareId.startsWith("resource-residual:")));
});

test("Every resource-fact field and every facts metric declares additivity", () => {
  // The catalog wire of the backend registry: every field that supports resource facts declares a boolean.
  const catalog = processFieldCatalogDecoder.decode([...staticProcessFieldCatalog(), gpuEngineField(0, 23, 0, 1), gpuResidentField(0, 23, "dedicated_resident_apportioned")]);
  for (const field of catalog.filter(field => field.supportsResourceFacts)) assert.equal(typeof field.additiveAcrossProcesses, "boolean", field.name);
  for (const metric of frame.metrics) assert.equal(typeof metric.additiveAcrossProcesses, "boolean", metric.metricId);
});

// The pre-r4 capture remains historical evidence, not a pending assertion against a superseded wire.
// SamplerBarsFeedProductionDecodersAndLocalizedNames covers the production disk.io wire in both states;
// backendWireAcceptanceDecoder.ts retains the item + total-or-reason assertion on those payloads.
const preRound4 = JSON.parse(readFileSync(new URL("./fixtures/resourceFactsAccounting.pre-r4.real.json", import.meta.url), "utf8"));
assert.equal(preRound4.privacyScan.passed, true);
test("A requested disk.io has a frame item with its total or an unavailable reason", () => {
  for (const total of [12, null]) {
    const facts = decodeResourceFacts(authorityFrame([{ metricId: "disk.io", unit: "B/s", total,
      capacity: total, additive: true, rows: [] }])).facts!;
    const disk = facts.metrics.find(metric => metric.metricId === "disk.io");
    assert.ok(disk, "A requested disk.io must have a facts item");
    assert.ok(disk.totalValue !== null || !!disk.unavailableReason, "disk.io must publish its total or an unavailable reason");
  }
});
