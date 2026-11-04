import { metricDisplayValue } from "../../presentation/metricLabels.ts";
import type { MetricValue } from "../../types.ts";

/* Record bounded trends only for large slots and only when their source publishes; null readings break the line. */

export const cardTrendCapacity = 60;

export interface CardTrendPoint {
  /** Use null for a missing reading. */
  readonly value: number | null;
  /** Retain the displayed text so extrema match card formatting. */
  readonly display: string;
}

export type CardTrends = ReadonlyMap<string, readonly CardTrendPoint[]>;

/** Append only readings published by this frame; drop metrics absent from ids. */
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

/** Return the minimum and maximum samples, or null without readings. */
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

/** Right-align samples in normalized coordinates. Percentages use 0-100; other values use padded extrema. Missing readings break paths. */
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
