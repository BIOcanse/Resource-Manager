// 资源条先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md）：可选来源（按 resourceFactTotalMetricId 归到自己的条）、
// 订阅参数（ids/modes 与 measures/modes，明细也带 measures）、后端条接到项目上、名字和说明，以及条上 processFieldName / totalMetricId 的解码。
import assert from "node:assert/strict";
import { processFieldCatalogDecoder } from "../src/data/resourceMonitor/processFieldCatalog.ts";
import {
  buildResourceMonitorSubscriptionUrl,
  buildResourceProcessDetailsSubscriptionUrl,
  normalizeResourceBreakdownQuery,
  normalizeResourceMonitorQuery
} from "../src/data/resourceMonitor/resourceMonitorApi.ts";
import { decodeResourceBreakdownWireSnapshot } from "../src/api/resourceBreakdownWire.ts";
import {
  resourceBarQuery,
  resourceBarSourceChoices,
  resourceBarSourceLabel,
  resourceBarText,
  resourceBarViews
} from "../src/features/resourceBreakdown/resourceBarSources.ts";
import { uiText } from "../src/text.ts";
import type { ResourceBarSettings, ResourceBreakdownBar } from "../src/types.ts";

const totalFor = (quantity: string | null) => quantity === "working_set_including_shared" || quantity === "private_working_set" ? "memory.usage"
  : quantity === "private_commit" ? "virtualMemory.usage" : null;
const wireField = (name: string, quantity: string | null, supportsResourceFacts: boolean) => ({ name, valueKind: "unsigned64", unit: "B",
  sourceId: "windows.process.query", resource: "process", vendor: "all",
  reading: quantity === null ? null : { resource: "memory", quantity }, supportsResourceFacts,
  resourceFactTotalMetricId: supportsResourceFacts ? totalFor(quantity) : null });
const fields = processFieldCatalogDecoder.decode([
  wireField("process_all_handle_count_actual", null, false),
  wireField("process_all_working_set_actual", "working_set_including_shared", true),
  wireField("process_all_private_working_set_actual", "private_working_set", true),
  wireField("process_all_private_commit_actual", "private_commit", true),
  wireField("process_all_peak_private_commit_actual", "peak_private_commit", false)
]);
const quantities = uiText.resourceTable.memoryQuantities;

// 两条整机内存资源条有来源：自动选择在前，然后是后端说整机比较指标就是这条的内存口径；别的条没有。
assert.deepEqual(resourceBarSourceChoices("memory.usage", fields),
  [null, "process_all_working_set_actual", "process_all_private_working_set_actual"], "Physical memory lists the working-set fields");
assert.deepEqual(resourceBarSourceChoices("virtualMemory.usage", fields),
  [null, "process_all_private_commit_actual"], "Commit lists private commit");
// resourceFactTotalMetricId 必须给出（可以是 null），且只能是两个整机指标之一。
assert.throws(() => processFieldCatalogDecoder.decode([{ ...wireField("x", null, false), resourceFactTotalMetricId: undefined }]), /resourceFactTotalMetricId/);
assert.throws(() => processFieldCatalogDecoder.decode([{ ...wireField("x", null, false), resourceFactTotalMetricId: "cpu.usage" }]), /resourceFactTotalMetricId/);
assert.deepEqual(resourceBarSourceChoices("cpu.usage", fields), []);
assert.deepEqual(resourceBarSourceChoices("memory.usage", []), [], "No catalog yet: nothing to choose");
assert.equal(resourceBarSourceLabel(null, fields, "process_all_private_working_set_actual"), uiText.metricPicker.sourceAutomatic(quantities.private_working_set));
assert.equal(resourceBarSourceLabel(null, fields, null), uiText.metricPicker.sourceAutomatic(null));
assert.equal(resourceBarSourceLabel("process_all_private_commit_actual", fields, null), quantities.private_commit);

// 订阅：自动选择按 ids/modes；选了来源发这个字段的 measures，缩放按 modes=字段名:缩放，不带这条的 ids；没有按 id 的条时发空 ids。
const memory: ResourceBarSettings = { id: "resource-memory", metricId: "memory.usage", scaleMode: "active", source: "process_all_private_commit_actual" };
const cpu: ResourceBarSettings = { id: "resource-cpu", metricId: "cpu.usage", scaleMode: "capacity" };
assert.deepEqual(resourceBarQuery(memory), { metricId: "memory.usage", scaleMode: "active", source: "process_all_private_commit_actual" },
  "A chosen source keeps the bar's scale mode");
assert.deepEqual(resourceBarQuery({ ...memory, source: null }), { metricId: "memory.usage", scaleMode: "active", source: null });
assert.deepEqual(resourceBarQuery({ ...cpu, source: "process_all_private_commit_actual" }).source, null, "Only memory bars take a source");
const query = (bars: ResourceBarSettings[]) => new URL(buildResourceMonitorSubscriptionUrl(normalizeResourceMonitorQuery({
  scope: "bars", bars: bars.map(resourceBarQuery), processDetailSoftwareIds: [], sampleMetricIds: null, visibleColumnIds: [],
  sortColumnId: "impact", sortDirection: "desc", tableMode: "software" }), 1000), "http://local").searchParams;
const mixed = query([cpu, memory]);
assert.deepEqual(mixed.getAll("ids"), ["cpu.usage"]);
assert.deepEqual(mixed.getAll("modes"), ["cpu.usage:capacity", "process_all_private_commit_actual:active"]);
assert.deepEqual(mixed.getAll("measures"), ["process_all_private_commit_actual"]);
const measuresOnly = query([memory]);
assert.deepEqual(measuresOnly.getAll("ids"), [""], "Without id bars the subscription says so instead of asking for the default bars");
assert.deepEqual(measuresOnly.getAll("measures"), ["process_all_private_commit_actual"]);
assert.deepEqual(query([memory, { ...memory, id: "resource-virtual-memory", metricId: "virtualMemory.usage" }]).getAll("measures"),
  ["process_all_private_commit_actual"], "The same measure is subscribed once");

// 进程明细和条一样带 measures：选了来源的资源条展开后按这个字段取进程。
const details = new URL(buildResourceProcessDetailsSubscriptionUrl(normalizeResourceBreakdownQuery({
  bars: [cpu, memory].map(resourceBarQuery), processDetailSoftwareIds: ["example"] }), 1000), "http://local").searchParams;
assert.equal(details.get("scope"), "details");
assert.deepEqual(details.getAll("measures"), ["process_all_private_commit_actual"]);
assert.deepEqual(details.getAll("ids"), ["cpu.usage"]);
assert.deepEqual(details.getAll("processDetails"), ["example"]);

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
