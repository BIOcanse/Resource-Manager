import { sortedPairs } from "./reportSeriesAdapter.ts";
import type { SeriesSet } from "./seriesModel.ts";
import {
  validateView, viewLimits, xSeriesId,
  type AggregationMethod, type ReportView, type ViewAxisSide, type ViewIssue, type ViewRange
} from "./viewModel.ts";

/**
 * 视图的计算（docs/targeted_recording_views_2026-10-30.md §3：先录完，再看；视图在前端算）。
 * 全部是纯函数，输入输出用类型化数组，没有递归，每个循环的次数以输入长度为上限。
 * NaN = 缺口。
 */

export interface Points { x: Float64Array; y: Float64Array }

/** 已升序排好的 count 个值上的百分位（fraction 0–1），相邻两个秩之间线性插值。没有值时为 NaN。 */
export function percentileOfSorted(sorted: Float64Array, count: number, fraction: number): number {
  if (count <= 0) return Number.NaN;
  const position = (count - 1) * Math.min(1, Math.max(0, fraction));
  const lower = Math.floor(position);
  const upper = Math.min(count - 1, lower + 1);
  return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}

/** 取出有限值并升序排好（新数组）。 */
export function sortedFinite(values: Float64Array): Float64Array {
  let count = 0;
  for (let index = 0; index < values.length; index++) if (Number.isFinite(values[index])) count++;
  const out = new Float64Array(count);
  let next = 0;
  for (let index = 0; index < values.length; index++) if (Number.isFinite(values[index])) out[next++] = values[index];
  return out.sort();
}

/** 只留 x 落在 [min, max] 内的点；两端都是 null 时原样返回。 */
export function filterWindow(points: Points, range: ViewRange): Points {
  if (range.min === null && range.max === null) return points;
  const low = range.min ?? Number.NEGATIVE_INFINITY;
  const high = range.max ?? Number.POSITIVE_INFINITY;
  let count = 0;
  for (let index = 0; index < points.x.length; index++) if (points.x[index] >= low && points.x[index] <= high) count++;
  const x = new Float64Array(count);
  const y = new Float64Array(count);
  let next = 0;
  for (let index = 0; index < points.x.length; index++) {
    if (points.x[index] < low || points.x[index] > high) continue;
    x[next] = points.x[index];
    y[next++] = points.y[index];
  }
  return { x, y };
}

const percentileOf: Partial<Record<AggregationMethod, number>> = { p50: 0.5, p95: 0.95, p99: 0.99 };

/**
 * 固定窗口聚合。x 升序；窗口按 floor(x / window) 分（时间从录制开始、帧序号从 0 起对齐），
 * 每个有点的窗口出一个点，x 是窗口起点，y 是窗口里有限值的平均、最大、最小或百分位；
 * 窗口里只有缺口时 y 是 NaN。两个有点的窗口之间隔着空窗口时，在第一个空窗口的起点插一个 NaN，
 * 线在那里断开。输出最多 2n 个点。method 为 none 时原样返回。
 */
export function aggregateWindows(points: Points, method: AggregationMethod, window: number): Points {
  const { x, y } = points;
  if (method === "none" || x.length === 0) return points;
  const outX = new Float64Array(x.length * 2);
  const outY = new Float64Array(x.length * 2);
  const scratch = new Float64Array(x.length);
  let count = 0;
  let previousKey = Number.NaN;
  let index = 0;
  while (index < x.length) {
    const key = Math.floor(x[index] / window);
    let end = index;
    let values = 0;
    let sum = 0;
    let maximum = Number.NEGATIVE_INFINITY;
    let minimum = Number.POSITIVE_INFINITY;
    while (end < x.length && Math.floor(x[end] / window) === key) {
      const value = y[end];
      if (Number.isFinite(value)) {
        scratch[values++] = value;
        sum += value;
        if (value > maximum) maximum = value;
        if (value < minimum) minimum = value;
      }
      end++;
    }
    if (Number.isFinite(previousKey) && key > previousKey + 1) {
      outX[count] = (previousKey + 1) * window;
      outY[count++] = Number.NaN;
    }
    let value = Number.NaN;
    if (values > 0) {
      if (method === "mean") value = sum / values;
      else if (method === "max") value = maximum;
      else if (method === "min") value = minimum;
      else value = percentileOfSorted(scratch.subarray(0, values).sort(), values, percentileOf[method] ?? 0.5);
    }
    outX[count] = key * window;
    outY[count++] = value;
    previousKey = key;
    index = end;
  }
  return { x: outX.slice(0, count), y: outY.slice(0, count) };
}

/**
 * “横轴 = 另一条序列”：对 ys 的每个点，按时间找 xs 里最近的采样，时间差不超过 windowMs 时
 * 取它的值作横坐标，否则横坐标是 NaN（缺口，不画）。两条都按时间升序；两个指针各走一遍。
 */
export function alignByTime(xs: Points, ys: Points, windowMs: number): Points {
  const x = new Float64Array(ys.x.length);
  let cursor = 0;
  for (let index = 0; index < ys.x.length; index++) {
    const time = ys.x[index];
    while (cursor + 1 < xs.x.length && xs.x[cursor + 1] <= time) cursor++;
    let best = -1;
    let bestDistance = Number.POSITIVE_INFINITY;
    for (const candidate of [cursor, cursor + 1]) {
      if (candidate >= xs.x.length) continue;
      const distance = Math.abs(xs.x[candidate] - time);
      if (distance < bestDistance) {
        best = candidate;
        bestDistance = distance;
      }
    }
    x[index] = best >= 0 && bestDistance <= windowMs ? xs.y[best] : Number.NaN;
  }
  return { x, y: ys.y };
}

/**
 * 自动分箱数：Freedman–Diaconis，箱宽 2·IQR·n^(-1/3)，箱数 = ceil(值域 / 箱宽)，
 * 限制在 viewLimits.autoBins 内。所有值相同（值域为 0）或少于 2 个值时为 1 箱；
 * IQR 为 0 而值域不为 0 时箱宽为 0，取上限。编辑器把这个数写在分箱数旁边。
 */
export function freedmanDiaconisBins(sorted: Float64Array): number {
  const count = sorted.length;
  if (count < 2 || sorted[count - 1] - sorted[0] <= 0) return 1;
  const span = sorted[count - 1] - sorted[0];
  const iqr = percentileOfSorted(sorted, count, 0.75) - percentileOfSorted(sorted, count, 0.25);
  const width = 2 * iqr / Math.cbrt(count);
  const bins = width > 0 ? Math.ceil(span / width) : viewLimits.autoBins.max;
  return Math.min(viewLimits.autoBins.max, Math.max(viewLimits.autoBins.min, bins));
}

export interface Histogram { edges: Float64Array; counts: Float64Array; total: number }

/**
 * [low, high] 上等宽 bins 个箱，最后一个箱含右端。范围外的值不计。
 * low 不小于 high 时（只有一个值）是以它为中心、宽 1 的一个箱。
 */
export function histogram(sorted: Float64Array, bins: number, low: number, high: number): Histogram {
  if (!(high > low)) {
    low -= 0.5;
    high += 0.5;
    bins = 1;
  }
  const width = (high - low) / bins;
  const edges = new Float64Array(bins + 1);
  for (let index = 0; index <= bins; index++) edges[index] = low + width * index;
  edges[bins] = high;
  const counts = new Float64Array(bins);
  let total = 0;
  for (let index = 0; index < sorted.length; index++) {
    const value = sorted[index];
    if (value < low || value > high) continue;
    counts[Math.min(bins - 1, Math.floor((value - low) / width))]++;
    total++;
  }
  return { edges, counts, total };
}

/** 各箱边上的累计百分比：第 0 个边为 0，最后一个边为 100（没有值时全为 NaN）。 */
export function cumulativePercent(counts: Float64Array, total: number): Float64Array {
  const out = new Float64Array(counts.length + 1);
  let running = 0;
  for (let index = 0; index <= counts.length; index++) {
    out[index] = total > 0 ? running / total * 100 : Number.NaN;
    if (index < counts.length) running += counts[index];
  }
  return out;
}

/** 百分位曲线：横轴 0、step、2·step…100（%），纵轴是这个百分位上的值。 */
export function percentileCurve(sorted: Float64Array, step: number): Points {
  const steps = Math.ceil(100 / step - 1e-9);
  const x = new Float64Array(steps + 1);
  const y = new Float64Array(steps + 1);
  for (let index = 0; index <= steps; index++) {
    x[index] = Math.min(100, index * step);
    y[index] = percentileOfSorted(sorted, sorted.length, x[index] / 100);
  }
  return { x, y };
}

export const summaryStatKeys = ["mean", "low1", "low01", "max", "min"] as const;
export type SummaryStatKey = typeof summaryStatKeys[number];

/**
 * 摘要：平均、1% Low（第 1 百分位）、0.1% Low（第 0.1 百分位）、最大、最小。
 * “Low”取值的低端：帧率序列上就是常说的 1% Low FPS；帧时间序列上是最短的那一端。
 */
export function summaryStats(sorted: Float64Array): Record<SummaryStatKey, number> {
  const count = sorted.length;
  let sum = 0;
  for (let index = 0; index < count; index++) sum += sorted[index];
  return {
    mean: count > 0 ? sum / count : Number.NaN,
    low1: percentileOfSorted(sorted, count, 0.01),
    low01: percentileOfSorted(sorted, count, 0.001),
    max: count > 0 ? sorted[count - 1] : Number.NaN,
    min: count > 0 ? sorted[0] : Number.NaN
  };
}

export type PlotStyle = "line" | "area" | "points" | "bars";
export type PlotXMode = "time" | "frames" | "value" | "percent" | "category";

export interface PlotSeries {
  /** 对应视图的第几条纵轴序列，配色按它取。 */
  sourceIndex: number;
  seriesId: string;
  label: string;
  /** count = 直方图的计数，cdf = 叠加的累计百分比（单位 %）。 */
  role: "value" | "count" | "cdf";
  axis: ViewAxisSide;
  unit: string;
  style: PlotStyle;
}

export interface PlotModel {
  status: "ready" | "invalid" | "empty";
  issues: ViewIssue[];
  x: { mode: PlotXMode; unit: string; categories: Array<{ stat: SummaryStatKey; sourceIndex: number }> };
  axes: { left: { unit: string } | null; right: { unit: string } | null };
  /** uPlot 的对齐数据：第一列是 x（升序），其余各列对应 series；null = 缺口，undefined = 这一行没有这条序列的点。 */
  data: [number[], ...Array<Array<number | null | undefined>>];
  series: PlotSeries[];
  /** 数据的横向范围（自动范围用它）。 */
  xExtent: [number, number] | null;
  /** 直方图的自动分箱数（Freedman–Diaconis）；不是直方图时为 null。 */
  autoBins: number | null;
}

function emptyPlot(status: PlotModel["status"], issues: ViewIssue[]): PlotModel {
  return { status, issues, x: { mode: "time", unit: "ms", categories: [] }, axes: { left: null, right: null },
    data: [[]], series: [], xExtent: null, autoBins: null };
}

/**
 * 多条序列合成 uPlot 的一组对齐列：k 路归并，每步取各序列头部最小的 x，头部等于它的序列各出一个点，
 * 其余序列这一行是 undefined。同一序列内 x 相同的点各占一行，不合并。行数不超过总点数。
 */
export function mergeColumns(parts: readonly Points[]): { x: number[]; columns: Array<Array<number | null | undefined>> } {
  const heads = new Uint32Array(parts.length);
  let total = 0;
  for (const part of parts) total += part.x.length;
  const x: number[] = [];
  const columns = parts.map(() => [] as Array<number | null | undefined>);
  for (let row = 0; row < total; row++) {
    let next = Number.POSITIVE_INFINITY;
    for (let part = 0; part < parts.length; part++) {
      if (heads[part] < parts[part].x.length && parts[part].x[heads[part]] < next) next = parts[part].x[heads[part]];
    }
    if (next === Number.POSITIVE_INFINITY) break;
    x.push(next);
    for (let part = 0; part < parts.length; part++) {
      const head = heads[part];
      if (head < parts[part].x.length && parts[part].x[head] === next) {
        const value = parts[part].y[head];
        columns[part].push(Number.isFinite(value) ? value : null);
        heads[part] = head + 1;
      } else {
        columns[part].push(undefined);
      }
    }
  }
  return { x, columns };
}

const column = (values: Float64Array) => Array.from(values, (value) => Number.isFinite(value) ? value : null);

/** 视图 + 序列 → 一张图要画的东西。视图有问题时 status = invalid，不出数据。 */
export function buildViewPlot(view: ReportView, set: SeriesSet): PlotModel {
  const issues = validateView(view, set);
  if (issues.length > 0) return emptyPlot("invalid", issues);
  const xId = xSeriesId(view.x);
  const prepare = (seriesId: string): Points => {
    const series = set.byId.get(seriesId)!;
    return aggregateWindows(filterWindow({ x: series.x, y: series.y }, view.sampleWindow),
      view.aggregation.method, view.aggregation.window);
  };
  const xPoints = xId === null ? null : prepare(xId);
  const sources = view.y.map((item, sourceIndex) => {
    const series = set.byId.get(item.seriesId)!;
    const prepared = prepare(item.seriesId);
    const points = xPoints === null ? prepared : (() => {
      const aligned = alignByTime(xPoints, prepared, view.matchWindowMs);
      return sortedPairs(aligned.x, aligned.y);
    })();
    return { item, sourceIndex, series, points };
  });
  const xSeries = xId === null ? null : set.byId.get(xId)!;
  const valueMode = { mode: (view.x === "time" ? "time" : view.x === "frameIndex" ? "frames" : "value") as PlotXMode,
    unit: view.x === "time" ? "ms" : view.x === "frameIndex" ? "" : xSeries!.unit, categories: [] };
  const axisUnit = (side: ViewAxisSide, plotSeries: PlotSeries[]) =>
    plotSeries.find((item) => item.axis === side) ? { unit: plotSeries.find((item) => item.axis === side)!.unit } : null;
  const hasValue = (columns: Array<Array<number | null | undefined>>) =>
    columns.some((values) => values.some((value) => typeof value === "number"));

  if (view.kind === "line" || view.kind === "area" || view.kind === "scatter") {
    const merged = mergeColumns(sources.map((source) => source.points));
    const style: PlotStyle = view.kind === "scatter" ? "points" : view.kind;
    const series = sources.map((source): PlotSeries => ({ sourceIndex: source.sourceIndex, seriesId: source.series.id,
      label: source.series.label, role: "value", axis: source.item.axis, unit: source.series.unit, style }));
    if (!hasValue(merged.columns)) return emptyPlot("empty", []);
    return { status: "ready", issues: [], x: valueMode, axes: { left: axisUnit("left", series), right: axisUnit("right", series) },
      data: [merged.x, ...merged.columns], series, xExtent: [merged.x[0], merged.x[merged.x.length - 1]], autoBins: null };
  }

  if (view.kind === "histogram") {
    const sortedSources = sources.map((source) => sortedFinite(source.points.y));
    let pooledCount = 0;
    for (const values of sortedSources) pooledCount += values.length;
    const pooled = new Float64Array(pooledCount);
    let offset = 0;
    for (const values of sortedSources) {
      pooled.set(values, offset);
      offset += values.length;
    }
    pooled.sort();
    if (pooled.length === 0) return emptyPlot("empty", []);
    const low = view.range.x.min ?? pooled[0];
    const high = view.range.x.max ?? pooled[pooled.length - 1];
    const inRange = filterWindow({ x: pooled, y: pooled }, { min: low, max: high }).x;
    const autoBins = freedmanDiaconisBins(inRange);
    const bins = view.histogram.bins ?? autoBins;
    const unit = sources[0].series.unit;
    const series: PlotSeries[] = [];
    const columns: Array<Array<number | null | undefined>> = [];
    let edges: Float64Array | null = null;
    for (const [index, source] of sources.entries()) {
      const result = histogram(sortedSources[index], bins, low, high);
      edges = result.edges;
      columns.push([...column(result.counts), null]);
      series.push({ sourceIndex: source.sourceIndex, seriesId: source.series.id, label: source.series.label,
        role: "count", axis: "left", unit: "count", style: "bars" });
      if (view.histogram.showCdf) {
        columns.push(column(cumulativePercent(result.counts, result.total)));
        series.push({ sourceIndex: source.sourceIndex, seriesId: source.series.id, label: source.series.label,
          role: "cdf", axis: "right", unit: "%", style: "line" });
      }
    }
    const x = Array.from(edges!);
    return { status: "ready", issues: [], x: { mode: "value", unit, categories: [] },
      axes: { left: { unit: "count" }, right: view.histogram.showCdf ? { unit: "%" } : null },
      data: [x, ...columns], series, xExtent: [x[0], x[x.length - 1]], autoBins };
  }

  if (view.kind === "cdf") {
    const curves = sources.map((source) => percentileCurve(sortedFinite(source.points.y), view.percentileStep));
    const series = sources.map((source): PlotSeries => ({ sourceIndex: source.sourceIndex, seriesId: source.series.id,
      label: source.series.label, role: "value", axis: source.item.axis, unit: source.series.unit, style: "line" }));
    const columns = curves.map((curve) => column(curve.y));
    if (!hasValue(columns)) return emptyPlot("empty", []);
    const x = Array.from(curves[0].x);
    return { status: "ready", issues: [], x: { mode: "percent", unit: "%", categories: [] },
      axes: { left: axisUnit("left", series), right: axisUnit("right", series) },
      data: [x, ...columns], series, xExtent: [0, 100], autoBins: null };
  }

  // 摘要条形：按统计量分组，每组里每条序列一根条；每一行只属于一条序列，其他序列这一行是 undefined。
  const stats = sources.map((source) => summaryStats(sortedFinite(source.points.y)));
  const categories: PlotModel["x"]["categories"] = [];
  const x: number[] = [];
  const columns = sources.map(() => [] as Array<number | null | undefined>);
  for (const stat of summaryStatKeys) {
    for (let source = 0; source < sources.length; source++) {
      x.push(x.length);
      categories.push({ stat, sourceIndex: source });
      for (let other = 0; other < sources.length; other++) {
        const value = stats[source][stat];
        columns[other].push(other !== source ? undefined : Number.isFinite(value) ? value : null);
      }
    }
  }
  const series = sources.map((source): PlotSeries => ({ sourceIndex: source.sourceIndex, seriesId: source.series.id,
    label: source.series.label, role: "value", axis: source.item.axis, unit: source.series.unit, style: "bars" }));
  if (!hasValue(columns)) return emptyPlot("empty", []);
  return { status: "ready", issues: [], x: { mode: "category", unit: "", categories },
    axes: { left: axisUnit("left", series), right: axisUnit("right", series) },
    data: [x, ...columns], series, xExtent: [-0.5, x.length - 0.5], autoBins: null };
}
