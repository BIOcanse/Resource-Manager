import assert from "node:assert/strict";
import { decodeResourceBreakdownWireSnapshot } from "../src/api/resourceBreakdownWire.ts";
import { decodeResourceFacts, projectResourceFacts } from "../src/features/resourceTable/resourceFacts.ts";
import { isReservedSegment, isReservedProcess } from "../src/features/resourceBreakdown/resourceProcessIdentity.ts";
import { softwareDisplayName, resourceTableRowName, localizedMetricLabel } from "../src/presentation/metricLabels.ts";
import { applyLanguage, uiText } from "../src/text.ts";

await applyLanguage("en-US");
const residualId = "opaque-owner-7";
const ordinaryId = "resource-residual:memory.usage";
// Both catalog entries are projected before numeric cells; the residual's name must use totalMetricId.
for (const [metricId, totalMetricId, processName] of [
  ["process_all_private_commit_actual", "virtualMemory.usage", "System commit / kernel"],
  ["memory.usage", "memory.usage", "System / kernel / drivers"]
]) {
  const facts = decodeResourceFacts({
    version: 2, capturedAt: null,
    software: [
      { softwareId: residualId, name: "obsolete name", kind: "windows_system", attributionKind: "system-residual" },
      { softwareId: ordinaryId, name: "Ordinary app", kind: "other", attributionKind: "other" }
    ],
    processes: [
      { processId: -1, processStartKey: null, name: "", executablePath: null, userName: null, architecture: null, attributionKind: "system-residual" },
      { processId: 42, processStartKey: "100", name: "app.exe", executablePath: null, userName: null, architecture: null, attributionKind: "process" }
    ],
    attributions: [{ softwareId: residualId, processIndex: 0, baseScore: 0 }, { softwareId: ordinaryId, processIndex: 1, baseScore: 1 }],
    metrics: [{ metricId, totalMetricId, processFieldName: metricId, unit: "B", totalValue: 10, capacityValue: 100,
      totalSystemPercent: 10, processSumValue: 4, residualValue: 6, additiveAcrossProcesses: true, capturedAt: null, windowMilliseconds: null,
      quantity: null, unavailableReason: null, gpuEngine: null, engineClass: null, residualSource: null, sharedValue: null,
      software: [residualId, ordinaryId].map((softwareId, index) => ({ softwareId, value: index ? 4 : 6, systemPercent: index ? 4 : 6,
        processCount: index, baseScore: index, sharedValue: null,
        processes: [{ processIndex: index, value: index ? 4 : 6, systemPercent: index ? 4 : 6, softwarePercent: 100, baseScore: index, sharedValue: null }] })) }]
  }).facts!;
  for (const mode of ["software", "process"] as const) {
    const table = projectResourceFacts(facts, [], mode, "name", "asc");
    const residualRows = table.rows.filter(row => row.softwareId === residualId);
    // r4: the PID -1 leaf is not a process; the software view keeps it in the residual row, the process view shows it as an attribution row.
    assert.deepEqual(residualRows.map(row => row.kind), [mode === "software" ? "software" : "attribution"]);
    for (const row of residualRows) {
      assert.equal(isReservedSegment(row), true, "opaque residual key is recognised by its displayKind");
      assert.equal(row.totalMetricId, totalMetricId);
      assert.equal(resourceTableRowName(row), row.kind === "attribution" ? processName : uiText.resourceTable.systemResidualRow(localizedMetricLabel(totalMetricId)));
    }
    for (const row of table.rows.filter(row => row.softwareId === ordinaryId)) {
      assert.equal(isReservedSegment(row), false, "a misleading key prefix cannot turn an ordinary row into a residual");
      assert.equal(resourceTableRowName(row), row.kind === "process" ? "app.exe" : "Ordinary app");
    }
  }
}

// The bar decoder carries the same typed identity; naming reads the containing bar's metricId.
const bar = decodeResourceBreakdownWireSnapshot({ version: 8, capturedAt: null,
  softwareCatalog: [[residualId, "ignored", "windows_system", "system-residual"], [ordinaryId, "Ordinary app", "other", "other"]],
  bars: [{ metricId: "gpu.2.vram", unit: "B", scaleMode: "capacity", totalValue: 10, capacityValue: 100, totalSystemPercent: 10,
    processSumValue: 4, residualValue: 6, additiveAcrossProcesses: true, capturedAt: null,
    software: [[0, 6, 6, 0, 0, []], [1, 4, 4, 1, 1, []]] }]
}).bars![0];
assert.equal(isReservedSegment(bar.software[0]), true);
assert.equal(softwareDisplayName(bar.software[0], bar.metricId), uiText.resourceTable.systemResidualRow(localizedMetricLabel("gpu.2.vram")));
assert.equal(isReservedSegment(bar.software[1]), false);
assert.equal(softwareDisplayName(bar.software[1], bar.metricId), "Ordinary app");
const leaf = { processId: -1, name: "", value: 6, systemPercent: 6, softwarePercent: 100, attributionKind: "system-memory/unclassified" };
assert.equal(isReservedProcess(bar.software[0], leaf), true);
assert.equal(isReservedProcess(bar.software[1], leaf), false);
console.log("Typed residual identities use explicit fact/bar context and ignore software key spelling.");
