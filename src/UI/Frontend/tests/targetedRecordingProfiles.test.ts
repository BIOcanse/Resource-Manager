import assert from "node:assert/strict";
import {
  addItem, catalogForUse, catalogGroups, intervalPlaceholder, newItem, parseIntervalText, removeItem, sameItems, sourceOptions, updateItem,
  type CatalogEntry, type SelectionItem
} from "../src/features/itemSelection/itemSelectionModel.ts";
import {
  draftChanged, draftFromProfile, draftIssues, hasIssues, profilePatch
} from "../src/features/targetedReport/profiles/profileDrafts.ts";
import { maximumProfileItems, type RecordingProfile, type RecordingReport } from "../src/features/targetedReport/targetedRecordingApi.ts";
import { seriesFromReport, type SeriesLabels } from "../src/features/targetedReport/views/reportSeriesAdapter.ts";

const catalog: CatalogEntry[] = [
  { kind: "frames", id: "frames.present", unit: "ms", sources: [], naturalIntervalMs: null, uses: ["recording", "overlay"] },
  { kind: "software", id: "cpu.usage", unit: "%", sources: ["resource-breakdown"], naturalIntervalMs: null, uses: ["recording", "overlay"] },
  { kind: "metric", id: "cpu.usage", unit: "%", sources: ["cpu.usage@cpu0"], naturalIntervalMs: null, uses: ["recording", "overlay"] },
  { kind: "metric", id: "cpu.temperature", unit: "°C", sources: ["cpu.temperature@cpu0"], naturalIntervalMs: 2000, uses: ["recording", "overlay"] }
];
// 只给浮层的帧项（后端目录 uses 为 ["overlay"]）。
const overlayOnly: CatalogEntry[] = ["frames.fps", "frames.low1", "frames.low01"].map((id) =>
  ({ kind: "frames", id, unit: "FPS", sources: [], naturalIntervalMs: null, uses: ["overlay"] }));
const names = (kind: string, id: string) => `${kind} ${id}`;

// ---------- 选择器的逻辑 ----------
// 新项的默认值：来源自动、间隔留空、同步精度中。
assert.deepEqual(newItem(catalog[3]),
  { kind: "metric", id: "cpu.temperature", source: "auto", intervalMs: null, syncPrecision: "mid" });

let items: SelectionItem[] = [];
for (const entry of catalog) {
  const result = addItem(items, entry, maximumProfileItems, ["frames", "software", "metric"]);
  assert.ok("items" in result);
  items = result.items;
}
assert.equal(items.length, 4);
assert.deepEqual(addItem(items, catalog[0], 256, ["frames", "software", "metric"]), { refused: "duplicate" });
// 上限：配置 256，浮层 64；到上限时拒绝并给出原因。
assert.deepEqual(addItem(items, { ...catalog[0], id: "frames.other" }, 4, ["frames", "software", "metric"]), { refused: "limit" });
const full = Array.from({ length: 64 }, (_, index) => newItem({ ...catalog[2], id: `metric.${index}` }));
assert.deepEqual(addItem(full, catalog[3], 64, ["frames", "software", "metric"]), { refused: "limit" });
// 用途过滤：录制配置看不到只给浮层的项，浮层看得到全部；选择器按过滤后的目录分组。
const combined = [...catalog, ...overlayOnly];
assert.deepEqual(catalogForUse(combined, "recording").map((entry) => entry.id),
  ["frames.present", "cpu.usage", "cpu.usage", "cpu.temperature"]);
assert.deepEqual(catalogForUse(combined, "overlay").map((entry) => entry.id),
  ["frames.present", "cpu.usage", "cpu.usage", "cpu.temperature", "frames.fps", "frames.low1", "frames.low01"]);
assert.deepEqual(catalogGroups(catalogForUse(combined, "recording"), [], ["frames"], "", names)[0].entries.map((row) => row.entry.id),
  ["frames.present"]);
assert.deepEqual(catalogGroups(catalogForUse(combined, "overlay"), [], ["frames"], "", names)[0].entries.map((row) => row.entry.id),
  ["frames.fps", "frames.low01", "frames.low1", "frames.present"]);
assert.deepEqual(catalogForUse([{ ...catalog[0], uses: [] }], "overlay"), []);
// 种类过滤：不允许的种类不加，目录里也不列。
assert.deepEqual(addItem([], catalog[0], 32, ["software", "metric"]), { refused: "kind" });
assert.deepEqual(catalogGroups(catalog, [], ["software", "metric"], "", names).map((group) => group.kind), ["software", "metric"]);
// 搜索按名称和 id，每个词都要匹配；已选的项照样列出并标记。
const searched = catalogGroups(catalog, items, ["frames", "software", "metric"], "metric temp", names);
assert.deepEqual(searched.map((group) => group.entries.map((row) => [row.entry.id, row.selected])), [[["cpu.temperature", true]]]);
assert.deepEqual(catalogGroups(catalog, [], ["metric"], "nothing-matches", names), []);

assert.equal(removeItem(items, "metric", "CPU.USAGE").length, 3);
// 帧没有间隔：改间隔也保持 null。
assert.equal(updateItem(items, "frames", "frames.present", { intervalMs: 500 })[0].intervalMs, null);
assert.equal(updateItem(items, "metric", "cpu.usage", { intervalMs: 500, syncPrecision: "high" })[2].syncPrecision, "high");

// 间隔：空 = 自然间隔或配置默认；其余 100–60000 的整数。
assert.deepEqual(parseIntervalText(""), { ok: true, value: null });
assert.deepEqual(parseIntervalText(" 250 "), { ok: true, value: 250 });
assert.deepEqual(parseIntervalText("99"), { ok: false });
assert.deepEqual(parseIntervalText("60001"), { ok: false });
assert.deepEqual(parseIntervalText("1.5"), { ok: false });
assert.deepEqual(parseIntervalText("-100"), { ok: false });
assert.deepEqual(intervalPlaceholder(catalog[3], 1000), { origin: "natural", intervalMs: 2000 });
assert.deepEqual(intervalPlaceholder(catalog[2], 750), { origin: "profile-default", intervalMs: 750 });
assert.equal(intervalPlaceholder(catalog[0], 1000), null);
// 来源：自动加目录来源；存着的来源不在目录里时也列出，不悄悄换掉。
assert.deepEqual(sourceOptions(catalog[3], "auto"), ["auto", "cpu.temperature@cpu0"]);
assert.deepEqual(sourceOptions(catalog[3], "cpu.temperature@gone"), ["auto", "cpu.temperature@cpu0", "cpu.temperature@gone"]);
assert.equal(sameItems(items, structuredClone(items)), true);
assert.equal(sameItems(items, updateItem(items, "metric", "cpu.usage", { source: "cpu.usage@cpu0" })), false);

// ---------- 配置草稿 ----------
const saved: RecordingProfile = { id: "default", name: "默认", defaultIntervalMs: 1000, items };
const draft = draftFromProfile(saved);
assert.equal(draftChanged(draft, saved), false);
assert.equal(hasIssues(draftIssues(draft)), false);
assert.deepEqual(draftIssues({ ...draft, name: "  " }), { name: true, defaultInterval: false, itemsEmpty: false });
assert.equal(draftIssues({ ...draft, name: "x".repeat(65) }).name, true);
assert.equal(draftIssues({ ...draft, defaultIntervalText: "50" }).defaultInterval, true);
assert.equal(draftIssues({ ...draft, items: [] }).itemsEmpty, true);
// 新建的草稿总是未保存。
assert.equal(draftChanged({ ...draft, savedId: null }, undefined), true);
// PATCH 只带改过的字段；名称去掉首尾空白。
assert.deepEqual(profilePatch({ ...draft, name: " 新名称 " }, saved), { name: "新名称" });
assert.deepEqual(profilePatch({ ...draft, defaultIntervalText: "2000" }, saved), { defaultIntervalMs: 2000 });
const fewer = removeItem(items, "frames", "frames.present");
assert.deepEqual(profilePatch({ ...draft, items: fewer }, saved), { items: fewer });
assert.deepEqual(profilePatch(draft, saved), {});

// ---------- 按录制项出序列 ----------
const labels: SeriesLabels = {
  frameTime: "Frame time", frameRate: "Frame rate", software: "Target", system: "System",
  metric: (id) => `metric ${id}`, item: (kind, id) => `${kind} item ${id}`
};
const start = Date.parse("2026-10-30T00:00:00Z");
const at = (ms: number) => new Date(start + ms).toISOString();
const header = {
  id: "r1", softwareId: "game", softwareName: "Game", startedAt: at(0), endedAt: at(3000),
  maximumDurationSeconds: 600, status: "completed" as const, stopReason: "user-stop", incomplete: false
};
const itemReport = (kind: "frames" | "software" | "metric", id: string, unit: string) => ({
  kind, id, unit, selectedSource: "auto", actualSources: [], selectedIntervalMs: null, intervalOrigin: "profile-default",
  actualIntervalMs: 1000, syncPrecision: "mid" as const, pointCount: 0, gaps: []
});
const report: RecordingReport = {
  recording: { ...header, items: [
    itemReport("frames", "frames.present", "ms"),
    itemReport("metric", "cpu.temperature", "°C"),
    itemReport("software", "cpu.usage", "%")
  ] },
  summary: null,
  frameIntervals: [{ processId: 1, processStartKey: 1, source: 0, swapChain: 1, endedAt: at(100), durationMs: 10 }],
  fpsDistribution: [],
  resourceSamples: [
    // 各项间隔不同：第一次只采了软件项；第二次两项都有，温度为 null（缺口）；第三次只有温度。
    { capturedAt: at(0), software: { "cpu.usage": 10 }, system: {}, processes: [] },
    { capturedAt: at(1000), software: { "cpu.usage": 20 }, system: { "cpu.temperature": null }, processes: [] },
    { capturedAt: at(2000), software: {}, system: { "cpu.temperature": 55, "cpu.usage": 99 }, processes: [] }
  ]
};
const set = seriesFromReport(report, labels);
const resourceSeries = set.series.filter((series) => series.meta.scope !== "frames");
// 顺序同录制项；没选的 system cpu.usage 不出序列。
assert.deepEqual(resourceSeries.map((series) => [series.role, series.label, series.unit]), [
  ["system:cpu.temperature", "System · metric item cpu.temperature", "°C"],
  ["software:cpu.usage", "Target · software item cpu.usage", "%"]
]);
assert.deepEqual(Array.from(resourceSeries[0].x), [1000, 2000]);
assert.ok(Number.isNaN(resourceSeries[0].y[0]));
assert.equal(resourceSeries[0].y[1], 55);
assert.deepEqual(Array.from(resourceSeries[1].x), [0, 1000]);
assert.deepEqual(Array.from(resourceSeries[1].y), [10, 20]);
// 帧序列照旧。
assert.equal(set.series.filter((series) => series.meta.scope === "frames").length, 4);
// 录制项为空（旧报告由新后端返回 items: []）时走旧路径：按采样里出现的指标出序列。
const legacy = seriesFromReport({ ...report, recording: { ...header, items: [] } }, labels);
assert.deepEqual(legacy.series.filter((series) => series.meta.scope !== "frames").map((series) => series.role),
  ["software:cpu.usage", "system:cpu.temperature", "system:cpu.usage"]);
console.log("Item picker logic, profile drafts and per-item report series passed.");
