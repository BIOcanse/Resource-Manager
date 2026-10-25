// 资源条先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md）：可选来源、订阅参数（ids/modes 与 measures）、
// 后端条接到项目上、名字和说明、缩放，以及条上 processFieldName / totalMetricId 的解码。
import assert from "node:assert/strict";
import { processFieldCatalogDecoder } from "../src/data/resourceMonitor/processFieldCatalog.ts";
import { buildResourceMonitorSubscriptionUrl, normalizeResourceMonitorQuery } from "../src/data/resourceMonitor/resourceMonitorApi.ts";
import { decodeResourceBreakdownWireSnapshot } from "../src/api/resourceBreakdownWire.ts";
import {
  resourceBarQuery,
  resourceBarScaleModes,
  resourceBarSourceChoices,
  resourceBarSourceLabel,
  resourceBarText,
  resourceBarViews
} from "../src/features/resourceBreakdown/resourceBarSources.ts";
import { uiText } from "../src/text.ts";
import type { ResourceBarSettings, ResourceBreakdownBar } from "../src/types.ts";

const wireField = (name: string, quantity: string | null, supportsResourceFacts: boolean) => ({ name, valueKind: "unsigned64", unit: "B",
  sourceId: "windows.process.query", resource: "process", vendor: "all",
  reading: quantity === null ? null : { resource: "memory", quantity }, supportsResourceFacts });
const fields = processFieldCatalogDecoder.decode([
  wireField("process_all_handle_count_actual", null, false),
  wireField("process_all_working_set_actual", "working_set_including_shared", true),
  wireField("process_all_private_working_set_actual", "private_working_set", true),
  wireField("process_all_private_commit_actual", "private_commit", true),
  wireField("process_all_peak_private_commit_actual", "peak_private_commit", false)
]);
const quantities = uiText.resourceTable.memoryQuantities;

// 两条整机内存资源条有来源：自动选择在前，然后是后端标了 supportsResourceFacts 的内存口径；别的条没有。
const allSources = [null, "process_all_working_set_actual", "process_all_private_working_set_actual", "process_all_private_commit_actual"];
assert.deepEqual(resourceBarSourceChoices("memory.usage", fields), allSources);
assert.deepEqual(resourceBarSourceChoices("virtualMemory.usage", fields), allSources);
assert.deepEqual(resourceBarSourceChoices("cpu.usage", fields), []);
assert.deepEqual(resourceBarSourceChoices("memory.usage", []), [], "No catalog yet: nothing to choose");
assert.equal(resourceBarSourceLabel(null, fields, "process_all_private_working_set_actual"), uiText.metricPicker.sourceAutomatic(quantities.private_working_set));
assert.equal(resourceBarSourceLabel(null, fields, null), uiText.metricPicker.sourceAutomatic(null));
assert.equal(resourceBarSourceLabel("process_all_private_commit_actual", fields, null), quantities.private_commit);

// 订阅：自动选择按 ids/modes；选了来源只发这个字段的 measures，不再带这条的 ids/modes；没有按 id 的条时发空 ids。
const memory: ResourceBarSettings = { id: "resource-memory", metricId: "memory.usage", scaleMode: "active", source: "process_all_private_commit_actual" };
const cpu: ResourceBarSettings = { id: "resource-cpu", metricId: "cpu.usage", scaleMode: "capacity" };
assert.deepEqual(resourceBarQuery(memory), { metricId: "memory.usage", scaleMode: "capacity", source: "process_all_private_commit_actual" },
  "A chosen source is compared against capacity only");
assert.deepEqual(resourceBarQuery({ ...memory, source: null }), { metricId: "memory.usage", scaleMode: "active", source: null });
assert.deepEqual(resourceBarQuery({ ...cpu, source: "process_all_private_commit_actual" }).source, null, "Only memory bars take a source");
const query = (bars: ResourceBarSettings[]) => new URL(buildResourceMonitorSubscriptionUrl(normalizeResourceMonitorQuery({
  scope: "bars", bars: bars.map(resourceBarQuery), processDetailSoftwareIds: [], sampleMetricIds: null, visibleColumnIds: [],
  sortColumnId: "impact", sortDirection: "desc", tableMode: "software" }), 1000), "http://local").searchParams;
const mixed = query([cpu, memory]);
assert.deepEqual(mixed.getAll("ids"), ["cpu.usage"]);
assert.deepEqual(mixed.getAll("modes"), ["cpu.usage:capacity"]);
assert.deepEqual(mixed.getAll("measures"), ["process_all_private_commit_actual"]);
const measuresOnly = query([memory]);
assert.deepEqual(measuresOnly.getAll("ids"), [""], "Without id bars the subscription says so instead of asking for the default bars");
assert.deepEqual(measuresOnly.getAll("measures"), ["process_all_private_commit_actual"]);
assert.deepEqual(query([memory, { ...memory, id: "resource-virtual-memory", metricId: "virtualMemory.usage" }]).getAll("measures"),
  ["process_all_private_commit_actual"], "The same measure is subscribed once");

// 缩放：选了来源只剩容量；自动选择保留原有能力。
assert.deepEqual(resourceBarScaleModes(memory), ["capacity"]);
assert.deepEqual(resourceBarScaleModes({ ...memory, source: null }), ["capacity", "active"]);
assert.deepEqual(resourceBarScaleModes({ metricId: "disk.io", source: null }), ["active"]);

// 条上的 processFieldName / totalMetricId：后端只给进程内存类的条；写了就必须是非空字符串。
const wireBar = (metricId: string, extra: Record<string, unknown> = {}) => ({ metricId, unit: "B", scaleMode: "capacity",
  totalValue: 6, capacityValue: 24, totalSystemPercent: 25, software: [[0, 1, 4, 1, 5, []]], ...extra });
const decode = (bars: unknown[]) => decodeResourceBreakdownWireSnapshot({ version: 8, capturedAt: null,
  softwareCatalog: [["example", "Example", "other", "other"]], bars }).bars!;
const decoded = decode([
  wireBar("cpu.usage", { unit: "%" }),
  wireBar("memory.usage", { processFieldName: "process_all_private_working_set_actual" }),
  wireBar("process_all_private_commit_actual", { processFieldName: "process_all_private_commit_actual", totalMetricId: "virtualMemory.usage" })
]);
assert.deepEqual(decoded.map((bar) => [bar.processFieldName, bar.totalMetricId]),
  [[null, null], ["process_all_private_working_set_actual", null], ["process_all_private_commit_actual", "virtualMemory.usage"]]);
assert.throws(() => decode([wireBar("memory.usage", { processFieldName: "" })]), /processFieldName/);
assert.throws(() => decode([wireBar("memory.usage", { totalMetricId: 3 })]), /totalMetricId/);

// 后端的条接到项目上：自动选择找指标 id，选了来源找字段那条，按项目 id 显示；后端没发的条（总量读不到）就没有。
const views = resourceBarViews(decoded, [cpu, memory], fields);
assert.deepEqual(views.map((bar) => [bar.metricId, bar.source, bar.totalValue]),
  [["cpu.usage", null, 6], ["memory.usage", "process_all_private_commit_actual", 6]]);
assert.equal(views[1].label, uiText.resourceTable.memoryColumnBySource(quantities.private_commit));
assert.equal(views[1].basis, uiText.resourceBreakdown.commitBasis);
assert.deepEqual(resourceBarViews(decoded.filter((bar) => bar.metricId !== "process_all_private_commit_actual"), [cpu, memory], fields)
  .map((bar) => bar.metricId), ["cpu.usage"], "A missing measure bar is not replaced by the automatic one");
const automaticView = resourceBarViews(decoded, [{ ...memory, source: null }], fields)[0];
assert.equal(automaticView.label, uiText.metricLabel["memory.usage"]);
assert.equal(automaticView.basis, `${uiText.metricPicker.sourceAutomatic(quantities.private_working_set)}\n${uiText.resourceBreakdown.memoryBasis}`,
  "Automatic names the field the backend actually reads");

// 说明跟着口径：工作集写共享页，私有工作集同自动选择，未知字段写字段名本身。
assert.equal(resourceBarText({ metricId: "memory.usage", source: "process_all_working_set_actual", processFieldName: null }, fields).basis,
  uiText.resourceTable.memorySourceBasis.working_set_including_shared);
assert.equal(resourceBarText({ metricId: "memory.usage", source: "process_all_private_working_set_actual", processFieldName: null }, fields).basis,
  uiText.resourceBreakdown.memoryBasis);
assert.equal(resourceBarText({ metricId: "memory.usage", source: "process_all_unknown", processFieldName: null }, []).label,
  uiText.resourceTable.memoryColumnBySource("process_all_unknown"));
const _typed: ResourceBreakdownBar = views[0];
void _typed;

console.log("resourceBarSources: bar sources, measures query, bar views, text and wire decode passed");
