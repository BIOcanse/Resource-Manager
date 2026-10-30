/**
 * 定向录制结果页的序列模型（docs/targeted_recording_views_2026-10-30.md §3“视图”）。
 *
 * 视图只认这里的序列，不认报告的线格式：今天的报告由 reportSeriesAdapter.ts 转成序列，
 * 后端换成新线格式时只换适配器，视图、计算和模板都不动。
 *
 * - id：这份报告里唯一的序列 id，视图的纵轴和“横轴 = 序列”引用它。
 * - role：跨报告不变的身份（frames、frameRate、software:<指标>、system:<指标>）。模板按 role 存，
 *   套用到别的报告时按 role 找回这份报告里的 id，所以同一模板能用在任意一份报告上。
 * - kind：x 的含义。time = 距录制开始的毫秒数；frame = 帧序号（0 起）。
 * - x 升序；y 与 x 等长，NaN = 缺口（这一点没有值）。
 */
export type SeriesDomain = "time" | "frame";

export interface ReportSeries {
  id: string;
  role: string;
  label: string;
  /** 原始单位：ms、FPS、%、B、B/s、°C、MHz。 */
  unit: string;
  kind: SeriesDomain;
  x: Float64Array;
  y: Float64Array;
  meta: { scope: "frames" | "software" | "system"; metricId: string | null };
}

export interface SeriesSet {
  series: readonly ReportSeries[];
  byId: ReadonlyMap<string, ReportSeries>;
}

export function seriesSet(series: readonly ReportSeries[]): SeriesSet {
  return { series, byId: new Map(series.map((item) => [item.id, item])) };
}

/** 一个 role 在某个 x 含义下对应的序列 id；没有这个含义的就取这个 role 的第一条；没有这个 role 返回 null。 */
export function seriesIdForRole(set: SeriesSet, role: string, kind: SeriesDomain): string | null {
  let first: string | null = null;
  for (const item of set.series) {
    if (item.role !== role) continue;
    if (item.kind === kind) return item.id;
    first ??= item.id;
  }
  return first;
}
