import assert from "node:assert/strict";
import { decodeResourceFacts, projectResourceFacts } from "../src/features/resourceTable/resourceFacts.ts";
import { buildResourceMonitorSubscriptionUrl, normalizeResourceMonitorQuery } from "../src/data/resourceMonitor/resourceMonitorApi.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";

const raw = {
  version: 2, capturedAt: "2026-10-24T12:00:00Z", attributions: [],
  software: [{ softwareId: "a", name: "App A", kind: "other", attributionKind: "other" }, { softwareId: "b", name: "App B", kind: "other", attributionKind: "other" }],
  processes: [{ processId: 42, processStartKey: "9223372036854775807", name: "worker.exe", executablePath: "C:/worker.exe", userName: null, architecture: "x64", attributionKind: "process" },
    { processId: 43, processStartKey: "9223372036854775806", name: "second.exe", executablePath: null, userName: "user", architecture: null, attributionKind: "process" }],
  metrics: [{ metricId: "memory.usage", unit: "B", totalMetricId: null, processSumValue: null, totalValue: 64, capacityValue: 128, totalSystemPercent: 50, sharedValue: 8,
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
const empty = projectResourceFacts(decodeResourceFacts({ version: 2, capturedAt: null, software: [], processes: [], metrics: [], attributions: [] }).facts!, columns, "software", "impact", "desc");
assert.equal(empty.rows[0].status, "empty");
assert.equal(empty.rows[0].values.memory, undefined);
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
