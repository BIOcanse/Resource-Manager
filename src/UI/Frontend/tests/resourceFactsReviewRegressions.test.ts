import assert from "node:assert/strict";
import { createRoot, createSignal } from "solid-js";
import { createResourceFactsProjection, projectResourceFacts, type ResourceFacts } from "../src/features/resourceTable/resourceFacts.ts";
import type { ResourceBreakdownBar, ResourceSoftwareSegment, ResourceTableColumnSettings, ResourceTableViewMode } from "../src/types.ts";
import { resourceTableCellText } from "../src/presentation/metricLabels.ts";
import { projectResourceTableHeat } from "../src/features/resourceTable/resourceTableHeat.ts";

const time = "2026-10-24T12:00:00Z";
const columns = [{ id: "name", visible: true, width: 260 }, { id: "memory", visible: true, width: 110 }];
function software(id: string, name: string, value: number): ResourceSoftwareSegment {
  return { softwareId: id, name, kind: "other", displayKind: "other", value, systemPercent: value,
    processCount: 1, baseScore: 0, processes: [{ processId: Number(id) + 1, processStartKey: String(Number(id) + 1),
      name, executablePath: null, userName: null, architecture: null, attributionKind: "process", value,
      systemPercent: value, softwarePercent: 100, baseScore: 0 }] };
}
function metric(metricId: string, software: ResourceSoftwareSegment[]): ResourceBreakdownBar {
  return { metricId, unit: metricId === "memory.usage" ? "B" : "%", scaleMode: "capacity",
    totalValue: 10, capacityValue: 100, totalSystemPercent: 10, software };
}
function factsFor(groups: { name: string; memory?: number }[]): ResourceFacts {
  return { capturedAt: time, metrics: [metric("cpu.usage", groups.map((group, index) => software(String(index), group.name, 0))),
    metric("memory.usage", groups.flatMap((group, index) => group.memory === undefined ? [] : [software(String(index), group.name, group.memory)]))] };
}
function names(facts: ResourceFacts, sort: string, direction: "asc" | "desc", mode: ResourceTableViewMode = "software") {
  return projectResourceFacts(facts, columns, mode, sort, direction, "en-US").rows.filter(row => row.kind === mode).map(row => row.name);
}

// HEAD SortRows: accent-sensitive, case-insensitive primary comparison. Numeric ties always use
// ascending case-insensitive names, and equal names retain the original enumeration order.
const accents = factsFor([{ name: "resume", memory: 5 }, { name: "résumé", memory: 5 }]);
for (const mode of ["software", "process"] as const) {
  assert.deepEqual(names(accents, "name", "asc", mode), ["resume", "résumé"]);
  assert.deepEqual(names(accents, "name", "desc", mode), ["résumé", "resume"]);
  for (const direction of ["asc", "desc"] as const) assert.deepEqual(names(accents, "memory", direction, mode), ["resume", "résumé"]);
}
const numeric = factsFor([{ name: "Beta", memory: 10 }, { name: "resume", memory: 5 }, { name: "résumé", memory: 5 },
  { name: "alpha" }, { name: "Alpha" }, { name: "Zero", memory: 0 }, { name: "aaa", memory: 5 }]);
for (const mode of ["software", "process"] as const) {
  assert.deepEqual(names(numeric, "memory", "asc", mode), ["alpha", "Alpha", "Zero", "aaa", "resume", "résumé", "Beta"]);
  assert.deepEqual(names(numeric, "memory", "desc", mode), ["Beta", "aaa", "resume", "résumé", "Zero", "alpha", "Alpha"]);
  const allMissing = names(numeric, "gpu.9.usage", "desc", mode);
  assert.deepEqual(allMissing, ["aaa", "alpha", "Alpha", "Beta", "resume", "résumé", "Zero"]);
}

// HEAD ToProcessRows sorts grouped children by numeric keys, then ascending names, including
// text columns whose numeric sort keys are absent. Flat process views use text primary keys.
const grouped = software("1", "Group", 10);
grouped.processCount = 2;
grouped.processes = [software("2", "résumé", 5).processes[0], software("3", "resume", 5).processes[0]];
const groupedFacts = { capturedAt: time, metrics: [metric("memory.usage", [grouped])] };
for (const direction of ["asc", "desc"] as const) {
  const result = projectResourceFacts(groupedFacts, columns, "software", "name", direction, "en-US");
  assert.deepEqual(result.rows.filter(row => row.kind === "process").map(row => row.name), ["resume", "résumé"]);
}

const duplicates = software("99", "Metadata group", 10);
duplicates.processes = [
  { ...duplicates.processes[0], processId: 1, processStartKey: "1", name: "Worker.EXE", executablePath: "C:/APP/Worker.EXE" },
  { ...duplicates.processes[0], processId: 1, processStartKey: "2", name: "worker.exe", executablePath: "c:/app/worker.exe" },
  { ...duplicates.processes[0], processId: 2, processStartKey: "3", name: "Other.exe", executablePath: " " },
  { ...duplicates.processes[0], processId: 3, processStartKey: "4", name: " ", executablePath: null }
];
const metadataGroup = projectResourceFacts({ capturedAt: time, metrics: [metric("memory.usage", [duplicates])] }, columns, "software", "name", "asc").rows[1];
assert.deepEqual(metadataGroup.processIds, [1, 2, 3]);
assert.deepEqual(metadataGroup.processNames, ["Worker.EXE", "Other.exe"]);
assert.deepEqual(metadataGroup.executablePaths, ["C:/APP/Worker.EXE"]);

// Identity text belongs to the frontend projection and is read by the cell renderer.
const identitySoftware = software("100", "Identity group", 1);
identitySoftware.processes[0] = { ...identitySoftware.processes[0], userName: "TEST-PC\\operator", architecture: "x64" };
const identityRow = projectResourceFacts({ capturedAt: time, metrics: [metric("memory.usage", [identitySoftware])] }, columns, "process", "name", "asc").rows[1];
assert.equal(resourceTableCellText(identityRow.values.pid), "101");
assert.equal(resourceTableCellText(identityRow.values.user), "TEST-PC\\operator");
assert.equal(resourceTableCellText(identityRow.values.architecture), "x64");

// A ready dataset may contain one available adapter and one explicitly unavailable adapter.
// Null total, null percentage or null capacity each makes only that summary cell unavailable.
const gpuColumns = [0, 1, 2].map(index => ({ id: `gpu.${index}.usage`, visible: true, width: 108 }));
for (const missing of ["totalValue", "totalSystemPercent", "capacityValue"] as const) {
  const ready = metric("gpu.0.usage", []);
  const unavailable = { ...metric("gpu.1.usage", []), [missing]: null };
  const projected = projectResourceFacts({ capturedAt: time, metrics: [ready, unavailable] }, gpuColumns, "software", "impact", "desc");
  const cells = projectResourceTableHeat(projected.rows)[0].values;
  assert.equal(cells["gpu.0.usage"].value, 10);
  assert.equal(resourceTableCellText(cells["gpu.0.usage"]), "10.0%");
  assert.equal(cells["gpu.1.usage"].value, null);
  assert.equal(cells["gpu.1.usage"].percent, null);
  assert.equal(cells["gpu.1.usage"].availability, "Unavailable");
  assert.equal(resourceTableCellText(cells["gpu.1.usage"]), "N/A");
  assert.equal(cells["gpu.2.usage"], undefined);
  assert.equal(resourceTableCellText(cells["gpu.2.usage"]), "--");
}

// Deterministic complexity regression: a real 5,000-process group, repeated in two metrics.
// Count membership operations rather than imposing a machine-dependent frame-time threshold.
const count = 5_000;
const many = software("10000", "Large group", 50);
many.processCount = count;
many.processes = Array.from({ length: count }, (_, index) => ({ ...many.processes[0], processId: index + 1,
  processStartKey: String(index + 1), name: `process-${index}.exe`, executablePath: `C:/apps/process-${index}.exe`, value: 1 }));
const large = { capturedAt: time, metrics: [metric("cpu.usage", [many]), metric("memory.usage", [many])] };
let linearMembershipChecks = 0;
let longArrayScans = 0;
const originalHas = Set.prototype.has;
const originalIncludes = Array.prototype.includes;
let projected;
try {
  Set.prototype.has = function(value) { linearMembershipChecks++; return originalHas.call(this, value); };
  Array.prototype.includes = function(value, start) { if (this.length >= 1_000) longArrayScans++; return originalIncludes.call(this, value, start); };
  projected = projectResourceFacts(large, columns, "software", "name", "asc");
} finally {
  Set.prototype.has = originalHas;
  Array.prototype.includes = originalIncludes;
}
assert.equal(longArrayScans, 0);
assert.ok(linearMembershipChecks <= 6 * count + 20, `Membership checks must be linear: ${linearMembershipChecks}`);
const largeGroup = projected!.rows.find(row => row.kind === "software")!;
assert.equal(largeGroup.processIds!.length, count);
assert.equal(largeGroup.processNames!.length, count);
assert.equal(largeGroup.executablePaths!.length, count);
assert.equal(projected!.rows.length, count + 2);

// Exercise the production reactive projection, not a source-code pattern. Widths reach the
// table's independent column memo; facts, visibility, mode and sort invalidate row projection.
createRoot(dispose => {
  const [currentFacts, setFacts] = createSignal<ResourceFacts | null>(large);
  const [settings, setSettings] = createSignal<readonly ResourceTableColumnSettings[]>(columns);
  const [mode, setMode] = createSignal<ResourceTableViewMode>("software");
  const [sort, setSort] = createSignal<{ columnId: string; direction: "asc" | "desc" }>({ columnId: "name", direction: "asc" });
  let recomputations = 0;
  const result = createResourceFactsProjection(currentFacts, settings, mode, sort, (...args) => {
    recomputations++;
    return projectResourceFacts(...args);
  });
  const initial = result();
  assert.equal(recomputations, 1);
  for (let index = 0; index < 20; index++) {
    setSettings(columns.map(column => ({ ...column, width: column.width + index + 1 })));
    assert.strictEqual(result(), initial);
  }
  assert.equal(recomputations, 1, "width-only resizing must not reproject 5,000 process rows");
  setSort({ columnId: "memory", direction: "desc" }); result();
  assert.equal(recomputations, 2);
  setSettings(columns.map(column => ({ ...column, visible: column.id === "name" }))); result();
  assert.equal(recomputations, 3);
  setMode("process"); result();
  assert.equal(recomputations, 4);
  setFacts({ ...large, capturedAt: "2026-10-24T12:00:01Z" }); result();
  assert.equal(recomputations, 5);
  dispose();
});
console.log(JSON.stringify({ checked: "B1/B2/B3", processCount: count, longArrayScans, linearMembershipChecks, widthOnlyRecomputations: 0 }));
