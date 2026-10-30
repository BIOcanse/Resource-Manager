// 定向录制结果页的视图：视图模型、视图计算、报告 → 序列的适配、模板经前端偏好的往返、数值格式。
import assert from "node:assert/strict";
import { test } from "node:test";
import type { RecordingReport } from "../src/features/targetedReport/targetedRecordingApi.ts";
import { metricUnit, seriesFromReport, seriesLabelForId, sortedPairs, type SeriesLabels } from "../src/features/targetedReport/views/reportSeriesAdapter.ts";
import { seriesIdForRole } from "../src/features/targetedReport/views/seriesModel.ts";
import {
  aggregateWindows, alignByTime, buildViewPlot, cumulativePercent, filterWindow, freedmanDiaconisBins, histogram,
  mergeColumns, percentileCurve, percentileOfSorted, sortedFinite, summaryStats
} from "../src/features/targetedReport/views/viewCompute.ts";
import {
  compatibleSeries, createView, defaultViews, duplicateView, moveView, parseViewFields, updateView, validateView, viewDomain
} from "../src/features/targetedReport/views/viewModel.ts";
import {
  decodeTemplates, readViewTemplates, saveViewTemplates, templateFromViews, viewsFromTemplate
} from "../src/features/targetedReport/views/viewTemplates.ts";
import { formatElapsed, tickText, unitScale } from "../src/features/targetedReport/views/viewFormat.ts";
import { createFrontendPreferencesClient, pointPreferences } from "../src/preferences/frontendPreferences.ts";
import { applyByteUnitMode } from "../src/presentation/byteUnits.ts";

const f64 = (values: number[]) => Float64Array.from(values);
const list = (values: ArrayLike<number>) => Array.from(values);
const labels: SeriesLabels = {
  frameTime: "Frame time", frameRate: "Frame rate", software: "Target", system: "System",
  metric: (id) => `metric ${id}`
};

const start = Date.parse("2026-10-30T00:00:00Z");
const at = (milliseconds: number) => new Date(start + milliseconds).toISOString();
function makeReport(): RecordingReport {
  const frames = [[16, 10], [33, 20], [50, 0], [66, 40]] as const;
  return {
    recording: { id: "recording-1", softwareId: "game", softwareName: "Game", startedAt: at(0), endedAt: at(3000),
      maximumDurationSeconds: 600, status: "completed", stopReason: "user", incomplete: false },
    summary: null,
    frameIntervals: frames.map(([ended, duration]) => ({ processId: 1, processStartKey: 1, source: 0, swapChain: 1,
      endedAt: at(ended), durationMs: duration })),
    fpsDistribution: [],
    // 采样故意不按时间顺序：适配器负责排好。
    resourceSamples: [
      { capturedAt: at(2000), software: { "cpu.usage": 30, "memory.usage": 3 * 1024 * 1024 }, system: { "cpu.usage": 60 }, processes: [] },
      { capturedAt: at(1000), software: { "cpu.usage": 20, "memory.usage": null }, system: { "cpu.usage": 50, "cpu.temperature": 70 }, processes: [] },
      { capturedAt: "not a time", software: { "cpu.usage": 99 }, system: {}, processes: [] }
    ]
  };
}

test("the adapter turns a report into time and frame series with gaps as NaN", () => {
  const set = seriesFromReport(makeReport(), labels);
  assert.deepEqual(set.series.map((series) => series.id), [
    "frames.time", "frames.index", "frameRate.time", "frameRate.index",
    "software:cpu.usage", "software:memory.usage", "system:cpu.temperature", "system:cpu.usage"
  ]);
  const frameTime = set.byId.get("frames.time")!;
  assert.deepEqual(list(frameTime.x), [16, 33, 50, 66]);
  assert.deepEqual(list(frameTime.y), [10, 20, 0, 40]);
  assert.equal(frameTime.unit, "ms");
  assert.deepEqual(list(set.byId.get("frames.index")!.x), [0, 1, 2, 3]);
  const rate = list(set.byId.get("frameRate.time")!.y);
  assert.deepEqual(rate.slice(0, 2), [100, 50]);
  assert.ok(Number.isNaN(rate[2]), "a zero frame time is a gap in the frame rate");
  assert.equal(rate[3], 25);
  const cpu = set.byId.get("software:cpu.usage")!;
  assert.deepEqual(list(cpu.x), [1000, 2000], "unparsable sample times are dropped and samples are sorted");
  assert.deepEqual(list(cpu.y), [20, 30]);
  assert.equal(cpu.label, "Target · metric cpu.usage");
  const memory = set.byId.get("software:memory.usage")!;
  assert.ok(Number.isNaN(memory.y[0]) && memory.y[1] === 3 * 1024 * 1024);
  assert.equal(memory.unit, "B");
  const temperature = set.byId.get("system:cpu.temperature")!;
  assert.ok(Number.isNaN(temperature.y[1]), "a sample without the metric is a gap");
  assert.deepEqual([metricUnit("disk.read"), metricUnit("gpu.0.vram"), metricUnit("cpu.frequency"), metricUnit("cpu.usage")],
    ["B/s", "B", "MHz", "%"]);
  assert.equal(seriesIdForRole(set, "frames", "frame"), "frames.index");
  assert.equal(seriesIdForRole(set, "frames", "time"), "frames.time");
  assert.equal(seriesIdForRole(set, "software:gpu.usage", "time"), null);
  assert.equal(seriesLabelForId("frameRate", labels), "Frame rate");
  assert.equal(seriesLabelForId("system:gpu.usage", labels), "System · metric gpu.usage");
  assert.equal(seriesLabelForId("custom", labels), null);
  const sorted = sortedPairs(f64([3, Number.NaN, 1, 2]), f64([30, 0, 10, 20]));
  assert.deepEqual([list(sorted.x), list(sorted.y)], [[1, 2, 3], [10, 20, 30]]);
});

test("percentiles interpolate between ranks and windows filter by x", () => {
  const sorted = f64([10, 20, 30, 40]);
  assert.equal(percentileOfSorted(sorted, 4, 0.5), 25);
  assert.equal(percentileOfSorted(sorted, 4, 0.95), 38.5);
  assert.equal(percentileOfSorted(sorted, 4, 0), 10);
  assert.ok(Number.isNaN(percentileOfSorted(f64([]), 0, 0.5)));
  assert.deepEqual(list(sortedFinite(f64([3, Number.NaN, 1, 2]))), [1, 2, 3]);
  const window = filterWindow({ x: f64([0, 100, 200, 300]), y: f64([1, 2, 3, 4]) }, { min: 100, max: 200 });
  assert.deepEqual([list(window.x), list(window.y)], [[100, 200], [2, 3]]);
});

test("fixed windows aggregate per window and leave a gap across empty windows", () => {
  const points = { x: f64([0, 400, 900, 1100, 3500]), y: f64([10, 20, 30, 40, 50]) };
  const mean = aggregateWindows(points, "mean", 1000);
  assert.deepEqual(list(mean.x), [0, 1000, 2000, 3000]);
  assert.deepEqual(list(mean.y).map((value) => Number.isNaN(value) ? null : value), [20, 40, null, 50]);
  assert.deepEqual(list(aggregateWindows(points, "max", 1000).y).filter(Number.isFinite), [30, 40, 50]);
  assert.deepEqual(list(aggregateWindows(points, "min", 1000).y).filter(Number.isFinite), [10, 40, 50]);
  assert.equal(aggregateWindows(points, "p95", 1000).y[0], 29);
  assert.equal(aggregateWindows(points, "none", 1000), points);
  const gaps = aggregateWindows({ x: f64([0, 10]), y: f64([Number.NaN, 5]) }, "mean", 5);
  assert.deepEqual(list(gaps.x), [0, 5, 10]);
  assert.ok(Number.isNaN(gaps.y[0]) && Number.isNaN(gaps.y[1]) && gaps.y[2] === 5, "a window with only gaps stays a gap");
});

test("x = another series takes the nearest sample in time within the alignment window", () => {
  const xs = { x: f64([0, 1000, 2000]), y: f64([5, 6, 7]) };
  const ys = { x: f64([100, 1600, 5000]), y: f64([1, 2, 3]) };
  const aligned = alignByTime(xs, ys, 500);
  assert.deepEqual(list(aligned.x).map((value) => Number.isNaN(value) ? null : value), [5, 7, null]);
  assert.deepEqual(list(aligned.y), [1, 2, 3]);
});

test("Freedman–Diaconis bins, histogram counts, cumulative percent and percentile curves", () => {
  assert.equal(freedmanDiaconisBins(f64(Array.from({ length: 1000 }, (_, index) => index + 1))), 10);
  assert.equal(freedmanDiaconisBins(f64(Array.from({ length: 100 }, (_, index) => index + 1))), 5, "clamped to the minimum");
  assert.equal(freedmanDiaconisBins(f64([4, 4, 4])), 1);
  assert.equal(freedmanDiaconisBins(f64([1, 1, 1, 1, 1, 1, 1, 1, 1, 100])), 200, "zero IQR with a span takes the maximum");
  const result = histogram(f64([1, 2, 2, 3, 4]), 3, 1, 4);
  assert.deepEqual(list(result.edges), [1, 2, 3, 4]);
  assert.deepEqual(list(result.counts), [1, 2, 2]);
  assert.equal(result.total, 5);
  assert.deepEqual(list(cumulativePercent(result.counts, result.total)), [0, 20, 60, 100]);
  const single = histogram(f64([7]), 10, 7, 7);
  assert.deepEqual([list(single.edges), list(single.counts)], [[6.5, 7.5], [1]]);
  const curve = percentileCurve(f64([10, 20, 30, 40, 50]), 25);
  assert.deepEqual([list(curve.x), list(curve.y)], [[0, 25, 50, 75, 100], [10, 20, 30, 40, 50]]);
  const stats = summaryStats(f64(Array.from({ length: 1000 }, (_, index) => index + 1)));
  assert.equal(stats.mean, 500.5);
  assert.ok(Math.abs(stats.low1 - 10.99) < 1e-9 && Math.abs(stats.low01 - 1.999) < 1e-9);
  assert.deepEqual([stats.max, stats.min], [1000, 1]);
});

test("columns merge by x with null gaps and undefined absences", () => {
  const merged = mergeColumns([{ x: f64([0, 2]), y: f64([1, 2]) }, { x: f64([1, 2]), y: f64([Number.NaN, 4]) }]);
  assert.deepEqual(merged.x, [0, 1, 2]);
  assert.deepEqual(merged.columns, [[1, undefined, 2], [undefined, null, 4]]);
});

test("view model edits, moves, duplicates and validates against the report's series", () => {
  const set = seriesFromReport(makeReport(), labels);
  const view = createView("v1", "CPU", { y: [{ seriesId: "software:cpu.usage", axis: "left" }] });
  assert.deepEqual(validateView(view, set), []);
  const changed = updateView(view, { kind: "area", range: { x: { min: 500 } }, aggregation: { method: "mean" } });
  assert.deepEqual([changed.kind, changed.range.x, changed.aggregation], ["area", { min: 500, max: null }, { method: "mean", window: 1000 }]);
  assert.equal(view.kind, "line", "updates return a new view");
  const copy = duplicateView(changed, "v2", "CPU (copy)");
  assert.deepEqual([copy.id, copy.name, copy.range.x.min], ["v2", "CPU (copy)", 500]);
  assert.deepEqual(moveView(["a", "b", "c"], 2, -1), ["a", "c", "b"]);
  assert.deepEqual(moveView(["a", "b"], 0, -1), ["a", "b"]);
  assert.equal(viewDomain("frameIndex"), "frame");
  assert.deepEqual(compatibleSeries(set, "frameIndex").map((series) => series.id), ["frames.index", "frameRate.index"]);

  const mixed = updateView(view, { y: [{ seriesId: "software:cpu.usage", axis: "left" }, { seriesId: "software:memory.usage", axis: "left" }] });
  assert.deepEqual(validateView(mixed, set), [{ code: "unit-mixed" }]);
  const sides = updateView(mixed, { y: [{ seriesId: "software:cpu.usage", axis: "left" }, { seriesId: "software:memory.usage", axis: "right" }] });
  assert.deepEqual(validateView(sides, set), []);
  assert.deepEqual(validateView(updateView(sides, { kind: "histogram" }), set), [{ code: "unit-mixed" }],
    "a histogram bins every series together, so all of them share one unit");
  assert.deepEqual(validateView(updateView(view, { x: "frameIndex" }), set), [{ code: "series-domain", seriesId: "software:cpu.usage" }]);
  assert.deepEqual(validateView(updateView(view, { x: "series:frames.index" }), set), [{ code: "x-series-domain", seriesId: "frames.index" }]);
  assert.deepEqual(validateView(updateView(view, { range: { left: { min: 5, max: 5 } } }), set), [{ code: "range-order", range: "left" }]);
  assert.deepEqual(validateView(updateView(view, { kind: "histogram", histogram: { bins: 0 } }), set),
    [{ code: "value-out-of-limits", field: "bins" }]);
  assert.deepEqual(validateView(createView("v3", " ", {}), set), [{ code: "name-empty" }, { code: "no-series" }]);

  const defaults = defaultViews({ frameTime: "Frame time", frameTimeDistribution: "Distribution", softwareResources: "Resources" },
    (() => { let next = 0; return () => `d${++next}`; })());
  assert.deepEqual(defaults.map((item) => [item.id, item.kind, item.y.map((entry) => `${entry.seriesId}@${entry.axis}`)]), [
    ["d1", "line", ["frames.time@left"]],
    ["d2", "histogram", ["frames.time@left"]],
    ["d3", "line", ["software:cpu.usage@left", "software:memory.usage@right"]]
  ]);
  assert.equal(defaults[1].histogram.showCdf, true);
  assert.deepEqual(validateView(defaults[2], set), []);
  assert.deepEqual(validateView(defaults[0], seriesFromReport({ ...makeReport(), frameIntervals: [] }, labels)),
    [{ code: "series-missing", seriesId: "frames.time" }], "a default view without its series says so instead of disappearing");
  assert.throws(() => parseViewFields({ ...defaults[0], kind: "pie" }, "seriesId"));
});

test("plots: lines on two axes, histogram with CDF, percentile curve, summary bars, x = series", () => {
  const set = seriesFromReport(makeReport(), labels);
  const resources = buildViewPlot(createView("r", "Resources", { y: [
    { seriesId: "software:cpu.usage", axis: "left" }, { seriesId: "software:memory.usage", axis: "right" }] }), set);
  assert.equal(resources.status, "ready");
  assert.deepEqual(resources.axes, { left: { unit: "%" }, right: { unit: "B" } });
  assert.deepEqual(resources.data, [[1000, 2000], [20, 30], [null, 3 * 1024 * 1024]]);

  const frames = createView("h", "Distribution", { kind: "histogram", y: [{ seriesId: "frames.time", axis: "left" }],
    histogram: { bins: 4, showCdf: true } });
  const plot = buildViewPlot(frames, set);
  assert.deepEqual(plot.data[0], [0, 10, 20, 30, 40]);
  assert.deepEqual(plot.data[1], [1, 1, 1, 1, null]);
  assert.deepEqual(plot.data[2], [0, 25, 50, 75, 100]);
  assert.deepEqual(plot.series.map((series) => [series.role, series.axis, series.unit, series.style]),
    [["count", "left", "count", "bars"], ["cdf", "right", "%", "line"]]);
  // 帧时间 0、10、20、40：IQR = 25 − 7.5 = 17.5，箱宽 2·17.5/∛4 ≈ 22.05，ceil(40 / 22.05) = 2，限制到下限 5。
  assert.equal(plot.autoBins, 5);
  const ranged = buildViewPlot(updateView(frames, { range: { x: { min: 10, max: 30 } }, histogram: { bins: 2 } }), set);
  assert.deepEqual([ranged.data[0], ranged.data[1]], [[10, 20, 30], [1, 1, null]], "bins cover only range.x");

  const cdf = buildViewPlot(updateView(frames, { kind: "cdf", percentileStep: 10 }), set);
  assert.deepEqual([cdf.x.mode, cdf.data[0].length, cdf.data[0][5], cdf.data[1][0], cdf.data[1][5], cdf.data[1][10]],
    ["percent", 11, 50, 0, 15, 40]);
  assert.deepEqual(validateView(updateView(frames, { kind: "cdf", percentileStep: 50 }), set),
    [{ code: "value-out-of-limits", field: "percentileStep" }]);

  const summary = buildViewPlot(createView("s", "Summary", { kind: "summaryBars", y: [{ seriesId: "frameRate.time", axis: "left" }] }), set);
  assert.equal(summary.x.mode, "category");
  assert.deepEqual(summary.x.categories.map((category) => category.stat), ["mean", "low1", "low01", "max", "min"]);
  assert.deepEqual((summary.data[1] as number[]).map((value) => Number(value.toFixed(4))), [58.3333, 25.5, 25.05, 100, 25]);

  const scatter = buildViewPlot(createView("x", "CPU vs system", { kind: "scatter", x: "series:system:cpu.usage", matchWindowMs: 100,
    y: [{ seriesId: "software:cpu.usage", axis: "left" }] }), set);
  assert.deepEqual([scatter.x.mode, scatter.x.unit, scatter.data[0], scatter.data[1]], ["value", "%", [50, 60], [20, 30]]);

  const windowed = buildViewPlot(createView("w", "Window", { y: [{ seriesId: "frames.time", axis: "left" }],
    sampleWindow: { min: 30, max: 60 } }), set);
  assert.deepEqual(windowed.data, [[33, 50], [20, 0]]);
  assert.equal(buildViewPlot(createView("e", "Empty", { y: [{ seriesId: "frames.time", axis: "left" }], sampleWindow: { min: 5000, max: null } }), set).status, "empty");
  assert.equal(buildViewPlot(createView("i", "Invalid", {}), set).status, "invalid");
});

test("templates reference series by role, apply to another report and round-trip through the frontend preferences", async () => {
  const set = seriesFromReport(makeReport(), labels);
  const views = [
    createView("a", "Frames by index", { x: "frameIndex", y: [{ seriesId: "frames.index", axis: "left" }], range: { x: { min: 1, max: 3 } } }),
    createView("b", "CPU vs system", { kind: "scatter", x: "series:system:cpu.usage", y: [{ seriesId: "software:cpu.usage", axis: "right" }] })
  ];
  const template = templateFromViews("t1", "Mine", views, set);
  assert.deepEqual(template.views.map((view) => [view.x, view.y]), [
    ["frameIndex", [{ role: "frames", axis: "left" }]],
    ["series:system:cpu.usage", [{ role: "software:cpu.usage", axis: "right" }]]
  ]);
  assert.equal("id" in template.views[0], false);

  let next = 0;
  const applied = viewsFromTemplate(template, set, () => `n${++next}`);
  assert.deepEqual(applied.map((view) => [view.id, view.name, view.x, view.y[0].seriesId, view.range.x]), [
    ["n1", "Frames by index", "frameIndex", "frames.index", { min: 1, max: 3 }],
    ["n2", "CPU vs system", "series:system:cpu.usage", "software:cpu.usage", { min: null, max: null }]
  ]);
  const withoutFrames = seriesFromReport({ ...makeReport(), frameIntervals: [] }, labels);
  assert.equal(viewsFromTemplate(template, withoutFrames, () => "m")[0].y[0].seriesId, "frames",
    "a role the report lacks stays visible on the view instead of being dropped");

  const values = new Map<string, string>();
  const disk = { getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
  const client = createFrontendPreferencesClient(disk);
  assert.deepEqual(await readViewTemplates(client), []);
  const before = await client.read();
  const saved = await saveViewTemplates([template], client);
  assert.deepEqual(saved, [template]);
  const restarted = createFrontendPreferencesClient(disk);
  assert.deepEqual(await readViewTemplates(restarted), [template]);
  const after = await restarted.read();
  assert.deepEqual({ ...after, targetedReportViewTemplates: null }, { ...before, targetedReportViewTemplates: null },
    "saving templates leaves the other preference sections as they were");
  await restarted.save({ ...after, appearance: { ...after.appearance, theme: "dark" } }, "global");
  assert.deepEqual(await readViewTemplates(restarted), [template], "a global save keeps the templates");
  await restarted.reset();
  assert.deepEqual(await readViewTemplates(restarted), []);
  assert.deepEqual((await restarted.read()).targetedReportViewTemplates, pointPreferences().targetedReportViewTemplates);

  assert.throws(() => decodeTemplates([{ id: "x", name: "x", views: [] }]));
  assert.throws(() => decodeTemplates([template, template]), "duplicate ids are rejected");
  assert.throws(() => decodeTemplates({}));
});

test("axis values: byte axes pick one unit from the largest value, time shows seconds", () => {
  applyByteUnitMode("native");
  const mib = 1024 * 1024;
  const memory = unitScale("B", 1.5 * mib);
  assert.equal(memory.unit, "MiB");
  assert.equal(memory.format(mib), "1.00 MiB");
  assert.equal(memory.format(512 * 1024), "0.50 MiB", "one unit for the whole axis");
  assert.equal(tickText(memory, 512 * 1024), "0.50");
  assert.equal(unitScale("B", 3 * 1024 * mib).unit, "GiB");
  const network = unitScale("B/s", 2_500_000);
  assert.equal(network.unit, "MB/s");
  assert.equal(network.format(2_500_000), "2.50 MB/s");
  applyByteUnitMode("binary");
  assert.equal(unitScale("B/s", 2 * mib).unit, "MiB/s");
  applyByteUnitMode("native");
  const cpu = unitScale("%", 40);
  assert.deepEqual([cpu.unit, cpu.format(40.123), tickText(cpu, 40)], ["%", "40.12 %", "40"]);
  assert.equal(unitScale("", 3).format(3), "3");
  assert.equal(formatElapsed(1500), "1.5 s");
});
