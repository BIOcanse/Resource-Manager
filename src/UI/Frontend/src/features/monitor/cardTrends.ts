import { metricDisplayValue } from "../../presentation/metricLabels.ts";
import type { MetricValue } from "../../types.ts";

/*
 * 大、超大槽位上的趋势：这个监控项最近 cardTrendCapacity 次读数（约一分钟，按监视刷新间隔）。
 * 每来一帧送它的那一路（原有指标帧或设备字段帧），给它记一点：数值和卡片上显示的那串字；
 * 没有读数时记空，画线时断开。只记卡片上大、超大槽位里的监控项，换掉或改小后它的记录就不要了。
 */

export const cardTrendCapacity = 60;

export interface CardTrendPoint {
  /** 这次的数值；没有读数时为 null。 */
  readonly value: number | null;
  /** 卡片上显示的那串字，最低、最高按它写，和卡片的格式一样。 */
  readonly display: string;
}

export type CardTrends = ReadonlyMap<string, readonly CardTrendPoint[]>;

/**
 * 记下一帧：ids 是现在要记趋势的监控项；updated(id) 说这一帧是不是送它的那一路来的，是才记一点。
 * 返回新的表，不在 ids 里的监控项不再保留。
 */
export function appendCardTrends(
  current: CardTrends,
  ids: ReadonlySet<string>,
  updated: (metricId: string) => boolean,
  items: Readonly<Record<string, MetricValue | undefined>>
): CardTrends {
  const next = new Map<string, readonly CardTrendPoint[]>();
  for (const metricId of ids) {
    const points = current.get(metricId) ?? [];
    if (!updated(metricId)) {
      next.set(metricId, points);
      continue;
    }
    const item = items[metricId];
    const raw = item && (item.status === undefined || item.status === 1) ? item.numericValue : null;
    const value = typeof raw === "number" && Number.isFinite(raw) ? raw : null;
    const point: CardTrendPoint = { value, display: value === null ? "" : metricDisplayValue(item, "") };
    next.set(metricId, [...points, point].slice(-cardTrendCapacity));
  }
  return next;
}

/** 记录里最低、最高的那两点（没有数值时为 null）。 */
export function cardTrendRange(points: readonly CardTrendPoint[]): { low: CardTrendPoint; high: CardTrendPoint } | null {
  let low: CardTrendPoint | null = null;
  let high: CardTrendPoint | null = null;
  for (const point of points) {
    if (point.value === null) continue;
    if (!low || point.value < low.value!) low = point;
    if (!high || point.value > high.value!) high = point;
  }
  return low && high ? { low, high } : null;
}

/**
 * 画趋势的路径，坐标在 0..100 × 0..100 的框里（SVG viewBox），新点在右边：第 i 点的横坐标按满 cardTrendCapacity 点排，
 * 记录不满时线从右边长出来。纵向范围：百分比固定 0–100，其余按记录里的最低、最高留一点边；没有起伏时画在中间。
 * 没有读数的点把线断开。line 是折线，area 是线下面到底边的面。
 */
export function cardTrendPaths(points: readonly CardTrendPoint[], percent: boolean): { line: string; area: string } {
  const range = cardTrendRange(points);
  if (!range) return { line: "", area: "" };
  const span = range.high.value! - range.low.value!;
  const [bottom, top] = percent
    ? [0, Math.max(100, range.high.value!)]
    : span === 0 ? [range.low.value! - 1, range.low.value! + 1] : [range.low.value! - span * 0.12, range.high.value! + span * 0.12];
  const offset = cardTrendCapacity - points.length;
  const x = (index: number) => ((offset + index) / (cardTrendCapacity - 1)) * 100;
  const y = (value: number) => 100 - ((value - bottom) / (top - bottom)) * 100;
  const runs: { x: number; y: number }[][] = [];
  let run: { x: number; y: number }[] = [];
  points.forEach((point, index) => {
    if (point.value === null) {
      if (run.length) runs.push(run);
      run = [];
      return;
    }
    run.push({ x: x(index), y: y(point.value) });
  });
  if (run.length) runs.push(run);
  const fixed = (value: number) => value.toFixed(2);
  const line = runs.map((points) => points.length === 1
    ? `M${fixed(points[0].x - 0.6)} ${fixed(points[0].y)}H${fixed(points[0].x)}`
    : points.map((point, index) => `${index ? "L" : "M"}${fixed(point.x)} ${fixed(point.y)}`).join("")).join("");
  const area = runs.map((points) => `M${fixed(points[0].x)} 100${points.map((point) => `L${fixed(point.x)} ${fixed(point.y)}`).join("")}L${fixed(points.at(-1)!.x)} 100Z`).join("");
  return { line, area };
}
