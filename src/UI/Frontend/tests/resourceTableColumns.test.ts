import assert from "node:assert/strict";
import { addTableColumn, fitTableColumnWidth, removeTableColumn, tableFieldCatalog, tableSampleItems } from "../src/features/resourceTable/resourceTableColumns.ts";
import { clampResourceTableColumnWidth, normalizeResourceTableColumns } from "../src/stores/monitorConfig.ts";
import { processFieldCatalogDecoder } from "../src/data/resourceMonitor/processFieldCatalog.ts";
import { catalogField } from "./processFieldCatalogFixture.ts";
import { ResourceManager } from "../src/preferences/generated/frontendPoints.values.ts";
import { decodeResourceFacts, projectResourceFacts } from "../src/features/resourceTable/resourceFacts.ts";
import { resourceTableCellText } from "../src/presentation/metricLabels.ts";
import { resourceTableMetricId } from "../src/features/resourceTable/resourceTableSources.ts";
import { accountResourceTableVisibility } from "../src/features/resourceTable/resourceTableAccounting.ts";

const name = "process_all_handle_count_actual", path = "process_all_image_path_actual";
const wire = [catalogField(name, "unsigned64", "count", "windows.process.query", { additive: true }),
  catalogField(path, "string", "text", "windows.process.query")];
const fields = processFieldCatalogDecoder.decode(wire);
assert.throws(() => processFieldCatalogDecoder.decode([{ ...wire[0], table: undefined }]), /table/);
assert.throws(() => processFieldCatalogDecoder.decode([{ ...wire[0], table: { scopes: ["global"], sources: [] } }]), /scopes/);
assert.ok(tableFieldCatalog(fields, "process").some(field => field.id === path));
assert.ok(!tableFieldCatalog(fields, "software").some(field => field.id === path));
let columns = addTableColumn([{ id: "name", visible: true }], name, fields, "process");
assert.equal(columns.length, 2);
assert.deepEqual(addTableColumn(columns, name, fields, "process"), columns);
assert.deepEqual(addTableColumn(columns, "unknown", fields, "process"), columns);
assert.deepEqual(removeTableColumn(columns, "name"), columns);
assert.equal(removeTableColumn(columns, name).length, 1);
assert.equal(resourceTableMetricId(columns[1]), name);
assert.ok(tableSampleItems(columns, [], fields).includes(name));
assert.ok(!tableSampleItems(removeTableColumn(columns, name), [], fields).includes(name));
assert.ok(!tableSampleItems(columns.map(column => ({ ...column, visible: false })), [], fields).includes(name));
assert.ok(!tableSampleItems(columns, [], []).includes(name), "An unavailable catalog item is not subscribed");
assert.equal(normalizeResourceTableColumns([{ id: name, visible: true }])[0].source, null);
const limits = ResourceManager.FrontendPoints.DashboardLayoutLimits;
assert.equal(clampResourceTableColumnWidth(-10), limits.minimumColumnWidth);
assert.equal(clampResourceTableColumnWidth(Infinity), limits.defaultColumnWidth);
assert.equal(fitTableColumnWidth(["wide"], () => 10000), limits.maximumColumnWidth);
assert.equal(fitTableColumnWidth(["short", "longer"], text => text.length * 10),
  Math.max(limits.minimumColumnWidth, 60 + ResourceManager.FrontendPoints.TableColumnFitPadding));

const raw = { version: 2, capturedAt: "2026-10-31T00:00:00Z", metrics: [],
  software: [{ softwareId: "a", name: "Example", kind: "other", attributionKind: "other" }],
  processes: [1, 2].map(pid => ({ processId: pid, processStartKey: `${100 + pid}`, name: `p${pid}`, executablePath: null,
    userName: null, architecture: null, attributionKind: "process" })),
  attributions: [0, 1].map(processIndex => ({ softwareId: "a", processIndex, baseScore: 0 })),
  fields: [{ field: name, unit: "count", sourceId: "windows.process.query", capturedAt: "2026-10-31T00:00:00Z", additiveAcrossProcesses: true,
    software: [], processes: ["9007199254740993", "9007199254740992"].map((integerValue, processIndex) => ({ processIndex,
      value: { field: name, integerValue, numericValue: null, stringValue: null }, limit: null, scopes: [] })) }] };
const facts = decodeResourceFacts(raw).facts!;
const projected = projectResourceFacts(facts, columns, "process", name, "asc", "en-US");
assert.deepEqual(projected.rows.slice(1).map(row => row.processId), [2, 1], "sort exact integers beyond JS precision");
assert.equal(resourceTableCellText(projected.rows[2].values[name]), "9007199254740993");
const accounted = accountResourceTableVisibility(projected.rows, [projected.rows[0]], "process", projected.columns);
assert.equal(resourceTableCellText(accounted.at(-1)!.values[name]), "18014398509481985", "Search accounting retains exact counts");
const textRaw = { ...raw, fields: [{ field: path, unit: "text", sourceId: "windows.process.query", capturedAt: raw.capturedAt,
  additiveAcrossProcesses: false, software: [], processes: ["z.exe", "a.exe"].map((stringValue, processIndex) => ({ processIndex,
    value: { field: path, integerValue: null, numericValue: null, stringValue }, limit: null, scopes: [] })) }] };
const textColumns = addTableColumn([{ id: "name", visible: true }], path, fields, "process");
const textRows = projectResourceFacts(decodeResourceFacts(textRaw).facts!, textColumns, "process", path, "asc", "en-US").rows.slice(1);
assert.deepEqual(textRows.map(row => row.processId), [2, 1]);
assert.equal(resourceTableCellText(textRows[0].values[path]), "a.exe");
assert.equal(projectResourceFacts(facts, removeTableColumn(columns, name), "process", "name", "asc", "en-US").columns.length, 1);
assert.throws(() => decodeResourceFacts({ ...raw, fields: [{ ...raw.fields[0], processes: [{ ...raw.fields[0].processes[0], processIndex: 99 }] }] }), /identity/);
assert.equal(resourceTableCellText({ unit: "%", value: 1, limit: 2.5 }), "1.0%/2.5%");
assert.equal(resourceTableCellText({ unit: "%", value: 1, limit: 0 }), "1.0%/0.0%");
assert.equal(resourceTableCellText({ unit: "%", value: 1, limit: null }), "1.0%");
console.log("Table capabilities, add/remove, resize bounds, typed values, exact sorting and limit contracts passed");
