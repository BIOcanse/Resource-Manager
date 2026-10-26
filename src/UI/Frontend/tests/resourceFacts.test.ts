import assert from "node:assert/strict";
import { decodeResourceFacts, projectResourceFacts } from "../src/features/resourceTable/resourceFacts.ts";
import { buildResourceMonitorSubscriptionUrl, normalizeResourceMonitorQuery } from "../src/data/resourceMonitor/resourceMonitorApi.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import { accountResourceTableVisibility } from "../src/features/resourceTable/resourceTableAccounting.ts";
import { uiText } from "../src/text.ts";
import { resourceFactUnavailableText, resourceProcessDisplayName, resourceTableCellText } from "../src/presentation/metricLabels.ts";
import { factsMetricKeys } from "./resourceFactsWireFixture.ts";

const raw = {
  version: 2, capturedAt: "2026-10-24T12:00:00Z", attributions: [],
  software: [{ softwareId: "a", name: "App A", kind: "other", attributionKind: "other" }, { softwareId: "b", name: "App B", kind: "other", attributionKind: "other" }],
  processes: [{ processId: 42, processStartKey: "9223372036854775807", name: "worker.exe", executablePath: "C:/worker.exe", userName: null, architecture: "x64", attributionKind: "process" },
    { processId: 43, processStartKey: "9223372036854775806", name: "second.exe", executablePath: null, userName: "user", architecture: null, attributionKind: "process" }],
  metrics: [{ metricId: "memory.usage", unit: "B", ...factsMetricKeys({ totalMetricId: "memory.usage", processSumValue: 64, residualValue: 0, additiveAcrossProcesses: true, capturedAt: "2026-10-24T12:00:00Z" }), processFieldName: null, totalValue: 64, capacityValue: 128, totalSystemPercent: 50, sharedValue: 8,
    software: [{ softwareId: "a", value: 16, systemPercent: 12.5, processCount: 1, baseScore: 0, sharedValue: 4, processes: [{ processIndex: 0, value: 16, systemPercent: 12.5, softwarePercent: 100, baseScore: 0, sharedValue: 4 }] },
      { softwareId: "b", value: 48, systemPercent: 37.5, processCount: 1, baseScore: 0, sharedValue: 4, processes: [{ processIndex: 1, value: 48, systemPercent: 37.5, softwarePercent: 100, baseScore: 0, sharedValue: 4 }] }] }]
};
const facts = decodeResourceFacts(raw).facts!;
const columns = [{ id: "name", visible: true, width: 200 }, { id: "memory", visible: true, width: 100 }, { id: "pid", visible: true, width: 76 }];
const descending = projectResourceFacts(facts, columns, "software", "memory", "desc");
assert.deepEqual(descending.rows.map(row => row.kind), ["summary", "software", "process", "software", "process"]);
assert.equal(descending.rows[1].softwareId, "b");
assert.equal(descending.rows[2].parentId, descending.rows[1].id);
assert.equal(descending.rows[0].values.memory.value, 64);
assert.equal(descending.rows[0].values.memory.sharedValue, 8);
assert.equal(descending.rows[4].processStartKey, "9223372036854775807");
assert.equal(descending.rows[4].values.user.displayValue, "--");
assert.equal(descending.rows[4].values.pid.displayValue, "42");
const ascending = projectResourceFacts(facts, columns, "process", "memory", "asc");
assert.deepEqual(ascending.rows.map(row => row.kind), ["summary", "process", "process"]);
assert.equal(ascending.rows[1].processId, 42);
assert.equal(ascending.rows[1].parentId, null);
assert.equal(ascending.rows[1].depth, 0);
assert.equal(ascending.rows[1].values.memory.sharedValue, 4);
const resized = projectResourceFacts(facts, [{ ...columns[0], width: 320 }, { ...columns[1], visible: false }], "software", "name", "asc");
assert.equal(resized.columns[0].width, 320);
assert.equal(resized.columns.length, 1);
assert.equal(resized.rows.length, 5);
assert.equal(resized.rows[1].name, "App A");
assert.equal(resized.rows[1].impactScore, ascending.rows[1].impactScore);
assert.equal(resized.rows[1].values.memory, undefined);
// 名称按自然顺序：带编号的名字按数值排（“示例软件 2”在“示例软件 10”前面），和资源管理器一样，不按逐字符。
const numbered = decodeResourceFacts({ ...raw, software: [{ ...raw.software[0], name: "Sample 10" }, { ...raw.software[1], name: "Sample 2" }] }).facts!;
assert.deepEqual(projectResourceFacts(numbered, columns, "software", "name", "asc").rows.filter(row => row.kind === "software").map(row => row.name),
  ["Sample 2", "Sample 10"]);
assert.deepEqual(projectResourceFacts(numbered, columns, "software", "name", "desc").rows.filter(row => row.kind === "software").map(row => row.name),
  ["Sample 10", "Sample 2"]);
const empty = projectResourceFacts(decodeResourceFacts({ version: 2, capturedAt: null, software: [], processes: [], metrics: [], attributions: [] }).facts!, columns, "software", "impact", "desc");
assert.equal(empty.rows[0].status, "empty");
assert.equal(empty.rows[0].values.memory.availability, "Unavailable");
assert.throws(() => decodeResourceFacts({ ...raw, processes: [{ ...raw.processes[0], processStartKey: 9223372036854775807 }] }), ResponseDecodeError);
assert.throws(() => decodeResourceFacts({ ...raw, processes: [] }), ResponseDecodeError);
const query = { scope: "table" as const, bars: [], sampleMetricIds: ["memory.usage"], visibleColumnIds: ["name", "memory"], sortColumnId: "memory", sortDirection: "asc" as const, tableMode: "process" as const, processDetailSoftwareIds: [] };
const normalized = normalizeResourceMonitorQuery(query);
assert.deepEqual(normalized, normalizeResourceMonitorQuery({ ...query, sortColumnId: "name", sortDirection: "desc", tableMode: "software", visibleColumnIds: ["memory", "name"] }));
const url = new URL(buildResourceMonitorSubscriptionUrl(normalized, 1700), "http://localhost");
assert.equal(url.pathname, "/api/resource-facts/subscribe");
assert.deepEqual(url.searchParams.getAll("items"), ["memory.usage"]);
assert.equal(url.searchParams.get("intervalMs"), "1700");
for (const key of ["columns", "sort", "mode", "scope", "includeProcesses", "expanded", "modes"]) assert.equal(url.searchParams.has(key), false);
console.log("resource facts decode, identity, grouping, sorting, columns and neutral subscription passed");

const namesOnly = projectResourceFacts(decodeResourceFacts({ ...raw, metrics: [],
  attributions: [{ processIndex: 0, softwareId: "a", baseScore: 0 }, { processIndex: 1, softwareId: "b", baseScore: 0 }] }).facts!,
  [{ id: "name", visible: true, width: 260 }], "software", "name", "asc");
assert.equal(namesOnly.rows.length, 5, "Identity rows do not need an unshown performance metric");
assert.equal(namesOnly.rows[1].values.memory, undefined);
assert.equal(namesOnly.rows[0].values.memory, undefined);

// metric-source round 4: each metric's residual is the software entry resource-residual:<totalMetricId> with PID -1 leaves
// (ResourceFactsSnapshot.Balance). Physical memory leaves are system-memory/... parts; other metrics have one system-residual leaf.
const ids = ["cpu", "memory", "gpu.0.usage", "gpu.0.vram", "disk", "network"];
const metricIds = ["cpu.usage", "memory.usage", "gpu.0.usage", "gpu.0.vram", "disk.io", "network.traffic"];
const leafKinds = (metricId: string) => metricId === "memory.usage"
  ? ["system-memory/nonpaged-pool/tag/41424344", "system-memory/unclassified"] : ["system-residual"];
const residualFrame = (() => {
  const software = [...raw.software];
  const processes = [...raw.processes];
  const attributions: { processIndex: number; softwareId: string; baseScore: number }[] = [
    { processIndex: 0, softwareId: "a", baseScore: 0 }, { processIndex: 1, softwareId: "b", baseScore: 0 }];
  const metrics = metricIds.map((metricId, index) => {
    const total = 100 + index;
    const residualId = `resource-residual:${metricId}`;
    software.push({ softwareId: residualId, name: "", kind: "windows_system", attributionKind: "system-residual" });
    const kinds = leafKinds(metricId);
    const residual = total - 64;
    const leaves = kinds.map((kind, leafIndex) => {
      const processIndex = processes.push({ processId: -1, processStartKey: null, name: "", executablePath: null, userName: null, architecture: null, attributionKind: kind }) - 1;
      attributions.push({ processIndex, softwareId: residualId, baseScore: 0 });
      const value = kinds.length === 1 ? residual : leafIndex === 0 ? 20 : residual - 20;
      return { processIndex, value, systemPercent: value / 2, softwarePercent: value * 100 / residual, baseScore: 0, sharedValue: null };
    });
    return { ...raw.metrics[0], metricId, unit: metricId.endsWith("usage") && metricId !== "memory.usage" ? "%" : metricId === "memory.usage" || metricId.endsWith(".vram") ? "B" : "B/s", sharedValue: null, totalValue: total, capacityValue: 200, totalSystemPercent: total / 2,
      ...factsMetricKeys({ totalMetricId: metricId, processSumValue: 64, residualValue: residual, additiveAcrossProcesses: true, capturedAt: raw.capturedAt,
        windowMilliseconds: metricId === "disk.io" || metricId === "network.traffic" ? 2000 : null }),
      software: [...raw.metrics[0].software.map(item => ({ ...item, sharedValue: null, processes: item.processes.map(process => ({ ...process, sharedValue: null })) })),
        { softwareId: residualId, value: residual, systemPercent: residual / 2, processCount: 0, baseScore: 0, sharedValue: null, processes: leaves }] };
  });
  return { ...raw, software, processes, attributions, metrics };
})();
const sameFrame = decodeResourceFacts(residualFrame).facts!;
const numericColumns = ids.map(id => ({ id, visible: true }));
for (const mode of ["software", "process"] as const) {
  const result = projectResourceFacts(sameFrame, numericColumns, mode, "name", "asc");
  assert.equal(result.capturedAt, raw.capturedAt);
  const isPeer = (row: typeof result.rows[number]) => mode === "software" ? row.kind === "software" : row.kind === "process" || row.kind === "attribution";
  assert.ok(result.rows.every(row => row.processId !== -1 && !row.processIds?.includes(-1)), "A leaf has no PID");
  for (const [index, id] of ids.entries()) {
    const metric = sameFrame.metrics[index];
    const peers = result.rows.filter(isPeer);
    const residualId = `resource-residual:${metricIds[index]}`;
    if (mode === "software") {
      assert.equal(peers.find(row => row.softwareId === residualId)!.values[id].value, 36 + index, "The residual entry carries its leaves' sum");
      assert.equal(peers.find(row => row.softwareId === residualId)!.processCount, 0, "Leaves are not processes");
      assert.ok(!result.rows.some(row => row.kind === "attribution"), "Software view shows the residual entry, not its leaves");
    } else {
      // Several leaves share PID -1 and an empty name; each stays its own row, keyed by its backend entry and kind.
      const leaves = peers.filter(row => row.kind === "attribution" && row.softwareId === residualId);
      assert.deepEqual(leaves.map(row => row.values.pid.attributionKind).sort(), [...leafKinds(metricIds[index])].sort());
      assert.equal(leaves.reduce((sum, row) => sum + row.values[id].value!, 0), 36 + index, "Backend residual leaves are preserved verbatim");
      for (const leaf of leaves) assert.equal(leaf.processCount, 0);
    }
    // The frame's own facts: total = process sum + signed residual (ResourceFactsSnapshot.Balance).
    assert.equal(metric.totalValue, metric.processSumValue! + metric.residualValue!, "BACKEND-RF-CLOSURE: total = processSum + residual");
    assert.equal(result.rows[0].values[id].value, peers.reduce((sum, row) => sum + (row.values[id]?.value ?? 0), 0),
      "BACKEND-RF-CLOSURE: total must equal attributed rows plus published residuals");
    const shown = result.rows.filter(row => row.kind === "summary" || row.softwareId === "a");
    const searched = accountResourceTableVisibility(result.rows, shown, mode, result.columns);
    assert.equal(searched.filter(row => isPeer(row) || row.kind === "filtered").reduce((sum, row) => sum + (row.values[id]?.value ?? 0), 0), 100 + index);
    assert.equal(searched.filter(row => row.kind === "filtered").length, 1);
    const noMatch = accountResourceTableVisibility(result.rows, [result.rows[0]], mode, result.columns);
    assert.equal(noMatch[1].values[id].value, 100 + index);
    assert.equal(noMatch[1].values[id].basis, undefined, "The bucket does not borrow the total's process sum and residual");
  }
  assert.strictEqual(accountResourceTableVisibility(result.rows, result.rows, mode, result.columns), result.rows, "Cleared search removes the bucket");
  if (mode === "software") {
    const collapsed = result.rows.filter(row => row.kind !== "process");
    assert.strictEqual(accountResourceTableVisibility(result.rows, collapsed, mode, result.columns), collapsed, "Collapsed children are already counted by their parent");
  }
}
// Leaf names come from resourceProcessDisplayName by kind; nothing is invented for an unnamed kind.
const processView = projectResourceFacts(sameFrame, numericColumns, "process", "name", "asc").rows;
assert.equal(processView.find(row => row.id === "attribution:resource-residual:cpu.usage:system-residual:-1:")!.name,
  resourceProcessDisplayName({ name: "", attributionKind: "system-residual" }, "cpu.usage"));
assert.equal(processView.find(row => row.id === "attribution:resource-residual:memory.usage:system-memory/unclassified:-1:")!.name,
  resourceProcessDisplayName({ name: "", attributionKind: "system-memory/unclassified" }, "memory.usage"));
// Summary cells explain each total from the same metric only: process sum, signed residual and its own window.
const cpuSummary = projectResourceFacts(sameFrame, numericColumns, "software", "name", "asc").rows[0];
assert.equal(cpuSummary.values.cpu.basis, uiText.resourceTable.factsBalance("64.0%", "36.0%"));
assert.equal(cpuSummary.values.disk.basis!.split("\n")[1], uiText.resourceTable.factsWindow("2"));
const negative = { ...sameFrame.metrics[0], totalValue: 60, residualValue: -4, software: sameFrame.metrics[0].software.map(item => item.softwareId === "resource-residual:cpu.usage"
  ? { ...item, value: -4, processes: item.processes.map(process => ({ ...process, value: -4 })) } : item) };
const negativeRows = projectResourceFacts({ metrics: [negative] }, numericColumns, "process", "name", "asc").rows;
assert.equal(negativeRows[0].values.cpu.basis, uiText.resourceTable.factsBalance("64.0%", "−4.0%"), "A negative residual keeps its sign");
assert.equal(negativeRows.find(row => row.kind === "attribution")!.values.cpu.value, -4, "A negative residual is shown, never scaled away");
// The frontend shows inconsistent backend totals verbatim instead of repairing them.
const inconsistent = { ...sameFrame, metrics: sameFrame.metrics.map(metric => ({ ...metric, totalValue: 999 })) };
const inconsistentResult = projectResourceFacts(inconsistent, numericColumns, "process", "name", "asc");
assert.equal(inconsistentResult.rows[0].values.cpu.value, 999, "Frontend displays inconsistent backend totals verbatim");
assert.equal(inconsistentResult.rows[0].values.cpu.availability, undefined);
assert.equal(inconsistentResult.rows.find(row => row.softwareId === "resource-residual:cpu.usage")!.values.cpu.value, 36, "Frontend does not repair the residual");
assert.throws(() => assert.equal(999, inconsistentResult.rows.slice(1).reduce((sum, row) => sum + (row.values.cpu?.value ?? 0), 0), "BACKEND-RF-CLOSURE"), /BACKEND-RF-CLOSURE/);
const noScale = { ...sameFrame.metrics[0], capacityValue: null, totalSystemPercent: null };
assert.equal(projectResourceFacts({ metrics: [noScale] }, numericColumns, "process", "name", "asc").rows[0].values.cpu.value, 100, "Missing scale facts do not hide a backend total");
// Working set is not additive: no residual value, no residual entry, and the note comes from the frame's own flag.
const workingSet = { ...sameFrame.metrics[1], metricId: "process_all_working_set_actual", processFieldName: "process_all_working_set_actual",
  additiveAcrossProcesses: false, residualValue: null, quantity: "working_set_including_shared",
  software: sameFrame.metrics[1].software.filter(item => !item.softwareId.startsWith("resource-residual:")) };
const workingSetTable = projectResourceFacts({ metrics: [workingSet] }, [{ id: "memory", visible: true, source: "process_all_working_set_actual" }], "process", "name", "asc");
const workingSetRows = workingSetTable.rows;
assert.equal(workingSetTable.columns.find(column => column.id === "memory")!.additiveAcrossProcesses, false, "The column carries the frame's additivity");
assert.equal(workingSetRows[0].values.memory.basis, uiText.resourceTable.nonAdditiveBasis);
assert.ok(!workingSetRows.some(row => row.kind === "attribution"), "A non-additive metric has no residual row");
const hidden = accountResourceTableVisibility(workingSetRows, [workingSetRows[0]], "process", workingSetTable.columns);
assert.equal(hidden[1].values.memory.value, null, "Hidden rows of a non-additive metric are not summed");
assert.equal(resourceTableCellText(hidden[1].values.memory, "memory.usage"), "--");
assert.equal(hidden[1].values.memory.basis, uiText.resourceTable.nonAdditiveBasis, "The hidden bucket says why it has no sum");
// A shared-only integrated GPU: gpu.N.vram is published unavailable with its reason and no rows.
const sharedOnly = decodeResourceFacts({ ...raw, attributions: [], metrics: [{ metricId: "gpu.1.vram", unit: "B", processFieldName: null,
  totalValue: null, capacityValue: null, totalSystemPercent: null, sharedValue: null, software: [],
  ...factsMetricKeys({ totalMetricId: "gpu.1.vram", processSumValue: null, residualValue: null, additiveAcrossProcesses: true, capturedAt: raw.capturedAt,
    quantity: "dedicated_memory_used", unavailableReason: "gpu-dedicated-memory-total-unavailable" }) }] }).facts!;
for (const mode of ["software", "process"] as const) {
  const rows = projectResourceFacts(sharedOnly, [{ id: "gpu.1.vram", visible: true }], mode, "name", "asc").rows;
  assert.equal(rows[0].values["gpu.1.vram"].availability, "Unavailable");
  assert.equal(rows[0].values["gpu.1.vram"].unavailableReason, "gpu-dedicated-memory-total-unavailable");
  assert.equal(resourceTableCellText(rows[0].values["gpu.1.vram"], "gpu.1.vram"),
    uiText.resourceTable.unavailableReasons["gpu-dedicated-memory-total-unavailable"]!.label);
  assert.ok(rows.slice(1).every(row => row.values["gpu.1.vram"] === undefined), "No numeric rows under an unavailable total");
}
assert.equal(resourceFactUnavailableText("future-reason").label, "future-reason", "An unknown reason code is shown as sent");
const absentSource = projectResourceFacts(sameFrame, [{ id: "cpu", visible: true, source: "absent_field" }], "software", "name", "asc");
assert.equal(absentSource.rows[0].values.cpu.availability, "Unavailable");
console.log("Round-4 per-metric facts, residual leaves, signed residuals, non-additive and unavailable metrics, and filtered accounting passed");
