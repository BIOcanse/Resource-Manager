import type { SeriesDomain, SeriesSet } from "./seriesModel.ts";

/**
 * 结果页的视图（docs/targeted_recording_views_2026-10-30.md §3“视图”）。视图只存用户的选择，
 * 不存算出来的东西；图怎么画由 viewCompute.ts 的 buildViewPlot 从视图和序列算出。
 *
 * - kind：折线、面积、散点、直方图、百分位（CDF，横轴为百分位、纵轴为该百分位上的值）、摘要条形。
 * - x：time = 距录制开始的毫秒数；frameIndex = 帧序号；series:<id> = 另一条序列的值，
 *   纵轴序列的每个点按时间找这条序列在 matchWindowMs 内最近的采样，找不到是缺口。
 * - y：一条或多条序列，各自放在左轴或右轴。
 * - aggregation：按横轴的源域（时间按毫秒、帧序号按帧）切成固定窗口，每个窗口出一个值。none = 原样。
 * - sampleWindow：源域上参与计算的数据范围（时间窗或帧区间），null = 不限。
 * - range：显示范围。x 是图上横轴的范围（直方图和百分位是值轴、百分位轴；分箱也只在这个范围内分），
 *   left、right 是两条纵轴；null = 自动。在图上框选会写 range.x，“重置范围”把它清回自动。
 * - histogram.bins：null = 自动（Freedman–Diaconis，限制在 autoBinLimits 内），否则就是这个分箱数。
 *   histogram.showCdf：在右轴叠加累计百分比。
 * - percentileStep：百分位视图横轴的步长（%）。
 */
export const viewKinds = ["line", "area", "scatter", "histogram", "cdf", "summaryBars"] as const;
export type ViewKind = typeof viewKinds[number];
export const aggregationMethods = ["none", "mean", "max", "min", "p50", "p95", "p99"] as const;
export type AggregationMethod = typeof aggregationMethods[number];
export type ViewAxisSide = "left" | "right";
export type ViewX = "time" | "frameIndex" | `series:${string}`;

export interface ViewYSeries { seriesId: string; axis: ViewAxisSide }
export interface ViewRange { min: number | null; max: number | null }

export interface ReportView {
  id: string;
  name: string;
  kind: ViewKind;
  x: ViewX;
  y: ViewYSeries[];
  aggregation: { method: AggregationMethod; window: number };
  matchWindowMs: number;
  sampleWindow: ViewRange;
  range: { x: ViewRange; left: ViewRange; right: ViewRange };
  histogram: { bins: number | null; showCdf: boolean };
  percentileStep: number;
}

/** 输入框的上下限，编辑器把它们写在 min、max 上。 */
export const viewLimits = {
  nameLength: 80,
  ySeries: 8,
  aggregationWindow: { time: { min: 1, max: 3_600_000 }, frame: { min: 1, max: 1_000_000 } },
  matchWindowMs: { min: 1, max: 60_000 },
  bins: { min: 1, max: 1000 },
  autoBins: { min: 5, max: 200 },
  percentileStep: { min: 0.01, max: 10 }
} as const;

/** 新视图的初始值；每一项都在编辑器里可见、可改。 */
export const viewDefaults = {
  kind: "line" as ViewKind,
  x: "time" as ViewX,
  aggregation: { method: "none" as AggregationMethod, window: 1000 },
  matchWindowMs: 1000,
  histogram: { bins: null, showCdf: false },
  percentileStep: 0.1
};

const autoRange = (): ViewRange => ({ min: null, max: null });

export function createView(id: string, name: string, fields: Partial<Omit<ReportView, "id" | "name">> = {}): ReportView {
  return {
    id, name,
    kind: fields.kind ?? viewDefaults.kind,
    x: fields.x ?? viewDefaults.x,
    y: (fields.y ?? []).map((item) => ({ ...item })),
    aggregation: { ...(fields.aggregation ?? viewDefaults.aggregation) },
    matchWindowMs: fields.matchWindowMs ?? viewDefaults.matchWindowMs,
    sampleWindow: { ...(fields.sampleWindow ?? autoRange()) },
    range: {
      x: { ...(fields.range?.x ?? autoRange()) },
      left: { ...(fields.range?.left ?? autoRange()) },
      right: { ...(fields.range?.right ?? autoRange()) }
    },
    histogram: { ...(fields.histogram ?? viewDefaults.histogram) },
    percentileStep: fields.percentileStep ?? viewDefaults.percentileStep
  };
}

export interface ViewPatch {
  name?: string;
  kind?: ViewKind;
  x?: ViewX;
  y?: ViewYSeries[];
  aggregation?: Partial<ReportView["aggregation"]>;
  matchWindowMs?: number;
  sampleWindow?: Partial<ViewRange>;
  range?: { x?: Partial<ViewRange>; left?: Partial<ViewRange>; right?: Partial<ViewRange> };
  histogram?: Partial<ReportView["histogram"]>;
  percentileStep?: number;
}

/** 改视图：返回新对象，没写到的字段原样保留。 */
export function updateView(view: ReportView, patch: ViewPatch): ReportView {
  return {
    id: view.id,
    name: patch.name ?? view.name,
    kind: patch.kind ?? view.kind,
    x: patch.x ?? view.x,
    y: (patch.y ?? view.y).map((item) => ({ ...item })),
    aggregation: { ...view.aggregation, ...patch.aggregation },
    matchWindowMs: patch.matchWindowMs ?? view.matchWindowMs,
    sampleWindow: { ...view.sampleWindow, ...patch.sampleWindow },
    range: {
      x: { ...view.range.x, ...patch.range?.x },
      left: { ...view.range.left, ...patch.range?.left },
      right: { ...view.range.right, ...patch.range?.right }
    },
    histogram: { ...view.histogram, ...patch.histogram },
    percentileStep: patch.percentileStep ?? view.percentileStep
  };
}

export function duplicateView(view: ReportView, id: string, name: string): ReportView {
  return { ...updateView(view, { name }), id };
}

/** 把 index 处的视图向前（-1）或向后（+1）移一位；越界时原样返回。 */
export function moveView<T>(views: readonly T[], index: number, delta: -1 | 1): T[] {
  const target = index + delta;
  const next = [...views];
  if (index < 0 || index >= views.length || target < 0 || target >= views.length) return next;
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}

export function xSeriesId(x: ViewX): string | null {
  return x.startsWith("series:") ? x.slice("series:".length) : null;
}

/** 横轴决定纵轴序列要用哪种 x：帧序号要帧序列，时间和“横轴 = 序列”（按时间对齐）要时间序列。 */
export function viewDomain(x: ViewX): SeriesDomain {
  return x === "frameIndex" ? "frame" : "time";
}

/** 这个横轴下能选作纵轴的序列。 */
export function compatibleSeries(set: SeriesSet, x: ViewX) {
  const domain = viewDomain(x);
  return set.series.filter((item) => item.kind === domain);
}

export type ViewIssue =
  | { code: "name-empty" }
  | { code: "no-series" }
  | { code: "too-many-series" }
  | { code: "series-missing"; seriesId: string }
  | { code: "series-domain"; seriesId: string }
  | { code: "x-series-missing"; seriesId: string }
  | { code: "x-series-domain"; seriesId: string }
  | { code: "unit-mixed" }
  | { code: "range-order"; range: "sampleWindow" | "x" | "left" | "right" }
  | { code: "value-out-of-limits"; field: "aggregationWindow" | "matchWindowMs" | "bins" | "percentileStep" };

/** 对照这份报告现有的序列检查视图；有问题的视图不画，问题逐条写在视图上。 */
export function validateView(view: ReportView, set: SeriesSet): ViewIssue[] {
  const issues: ViewIssue[] = [];
  if (!view.name.trim()) issues.push({ code: "name-empty" });
  if (view.y.length === 0) issues.push({ code: "no-series" });
  if (view.y.length > viewLimits.ySeries) issues.push({ code: "too-many-series" });
  const domain = viewDomain(view.x);
  for (const item of view.y) {
    const series = set.byId.get(item.seriesId);
    if (!series) issues.push({ code: "series-missing", seriesId: item.seriesId });
    else if (series.kind !== domain) issues.push({ code: "series-domain", seriesId: item.seriesId });
  }
  const xId = xSeriesId(view.x);
  if (xId !== null) {
    const series = set.byId.get(xId);
    if (!series) issues.push({ code: "x-series-missing", seriesId: xId });
    else if (series.kind !== "time") issues.push({ code: "x-series-domain", seriesId: xId });
  }
  // 一条纵轴只有一种单位（刻度按它换算）；直方图的所有序列共用一组分箱，所以全部序列同一单位。
  const unitGroups = view.kind === "histogram" ? [view.y] : [view.y.filter((item) => item.axis === "left"),
    view.y.filter((item) => item.axis === "right")];
  for (const group of unitGroups) {
    const units = new Set(group.map((item) => set.byId.get(item.seriesId)?.unit).filter((unit) => unit !== undefined));
    if (units.size > 1) {
      issues.push({ code: "unit-mixed" });
      break;
    }
  }
  const ordered = (range: ViewRange) => range.min === null || range.max === null || range.min < range.max;
  if (!ordered(view.sampleWindow)) issues.push({ code: "range-order", range: "sampleWindow" });
  for (const key of ["x", "left", "right"] as const) {
    if (!ordered(view.range[key])) issues.push({ code: "range-order", range: key });
  }
  const within = (value: number, limits: { min: number; max: number }) =>
    Number.isFinite(value) && value >= limits.min && value <= limits.max;
  if (view.aggregation.method !== "none" && !within(view.aggregation.window, viewLimits.aggregationWindow[domain])) {
    issues.push({ code: "value-out-of-limits", field: "aggregationWindow" });
  }
  if (xId !== null && !within(view.matchWindowMs, viewLimits.matchWindowMs)) {
    issues.push({ code: "value-out-of-limits", field: "matchWindowMs" });
  }
  if (view.kind === "histogram" && view.histogram.bins !== null
      && (!Number.isInteger(view.histogram.bins) || !within(view.histogram.bins, viewLimits.bins))) {
    issues.push({ code: "value-out-of-limits", field: "bins" });
  }
  if (view.kind === "cdf" && !within(view.percentileStep, viewLimits.percentileStep)) {
    issues.push({ code: "value-out-of-limits", field: "percentileStep" });
  }
  return issues;
}

export interface DefaultViewNames { frameTime: string; frameTimeDistribution: string; softwareResources: string }

/**
 * 每份报告自带的视图，和用户自己建的视图一样可见、可改、可删：
 * 帧时间（折线，横轴时间）、帧时间分布（直方图，叠加累计分布）、目标软件资源（折线，CPU 左轴、内存右轴）。
 * 三个都建，不看报告当前有没有这些序列：录制刚开始时还没有帧，序列到了图就出来；
 * 一直没有的（例如不出帧的软件），视图上写着“这份报告没有这条序列”，用户可以删掉。
 */
export const defaultViewSeries = {
  frameTime: "frames.time",
  softwareCpu: "software:cpu.usage",
  softwareMemory: "software:memory.usage"
} as const;

export function defaultViews(names: DefaultViewNames, nextId: () => string): ReportView[] {
  return [
    createView(nextId(), names.frameTime, { kind: "line", x: "time", y: [{ seriesId: defaultViewSeries.frameTime, axis: "left" }] }),
    createView(nextId(), names.frameTimeDistribution, { kind: "histogram", x: "time",
      y: [{ seriesId: defaultViewSeries.frameTime, axis: "left" }], histogram: { bins: null, showCdf: true } }),
    createView(nextId(), names.softwareResources, { kind: "line", x: "time", y: [
      { seriesId: defaultViewSeries.softwareCpu, axis: "left" }, { seriesId: defaultViewSeries.softwareMemory, axis: "right" }] })
  ];
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);

class ViewShapeError extends Error {}

function nullableNumber(value: unknown, path: string): number | null {
  if (value === null) return null;
  if (typeof value !== "number" || !Number.isFinite(value)) throw new ViewShapeError(path);
  return value;
}

function range(value: unknown, path: string): ViewRange {
  if (!isRecord(value)) throw new ViewShapeError(path);
  return { min: nullableNumber(value.min, `${path}.min`), max: nullableNumber(value.max, `${path}.max`) };
}

function finite(value: unknown, path: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) throw new ViewShapeError(path);
  return value;
}

function oneOf<T extends string>(value: unknown, options: readonly T[], path: string): T {
  if (typeof value !== "string" || !options.includes(value as T)) throw new ViewShapeError(path);
  return value as T;
}

/**
 * 从存储读回一个视图的字段（不含 id）：形状不对就抛错，不猜、不补。
 * seriesRef 决定纵轴引用的字段名（视图用 seriesId，模板用 role）。
 */
export function parseViewFields(value: unknown, seriesRef: "seriesId" | "role"):
  Omit<ReportView, "id" | "y"> & { y: Array<{ ref: string; axis: ViewAxisSide }> } {
  if (!isRecord(value)) throw new ViewShapeError("view");
  if (typeof value.name !== "string" || value.name.length > viewLimits.nameLength) throw new ViewShapeError("name");
  if (typeof value.x !== "string" || !(value.x === "time" || value.x === "frameIndex"
      || (value.x.startsWith("series:") && value.x.length > "series:".length))) throw new ViewShapeError("x");
  if (!Array.isArray(value.y) || value.y.length > viewLimits.ySeries) throw new ViewShapeError("y");
  const aggregation = value.aggregation;
  const histogram = value.histogram;
  const ranges = value.range;
  if (!isRecord(aggregation) || !isRecord(histogram) || !isRecord(ranges)) throw new ViewShapeError("view");
  if (typeof histogram.showCdf !== "boolean") throw new ViewShapeError("histogram.showCdf");
  return {
    name: value.name,
    kind: oneOf(value.kind, viewKinds, "kind"),
    x: value.x as ViewX,
    y: value.y.map((item, index) => {
      if (!isRecord(item) || typeof item[seriesRef] !== "string" || !(item[seriesRef] as string)) {
        throw new ViewShapeError(`y.${index}`);
      }
      return { ref: item[seriesRef] as string, axis: oneOf(item.axis, ["left", "right"] as const, `y.${index}.axis`) };
    }),
    aggregation: { method: oneOf(aggregation.method, aggregationMethods, "aggregation.method"),
      window: finite(aggregation.window, "aggregation.window") },
    matchWindowMs: finite(value.matchWindowMs, "matchWindowMs"),
    sampleWindow: range(value.sampleWindow, "sampleWindow"),
    range: { x: range(ranges.x, "range.x"), left: range(ranges.left, "range.left"), right: range(ranges.right, "range.right") },
    histogram: { bins: nullableNumber(histogram.bins, "histogram.bins"), showCdf: histogram.showCdf },
    percentileStep: finite(value.percentileStep, "percentileStep")
  };
}
