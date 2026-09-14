import type { MetricSnapshot } from "../types.ts";
import { metricSnapshotObservedAt } from
  "../data/monitor/metricSnapshotValue.ts";

export interface PerformancePoint {
  readonly capturedAt: string | null;
  readonly values: Readonly<Record<string, number | null>>;
  readonly displays: Readonly<Record<string, string>>;
}

export interface PerformanceSeries {
  readonly id: string;
  readonly label: string;
  readonly color: string;
}

export interface PerformanceRunPoint {
  readonly index: number;
  readonly value: number;
}

export function appendPerformancePoint(
  snapshot: MetricSnapshot,
  series: readonly PerformanceSeries[]
): PerformancePoint {
  const capturedAt = metricSnapshotObservedAt(snapshot)?.trim() || null;
  const items = snapshot.items;
  const values: Record<string, number | null> = {};
  const displays: Record<string, string> = {};
  for (const item of series) {
    const metric = items[item.id];
    const rawValue = metric?.numericValue;
    const value = typeof rawValue === "number" && Number.isFinite(rawValue)
      ? Math.max(0, Math.min(100, rawValue))
      : null;
    values[item.id] = value;
    displays[item.id] = metric?.displayValue ?? "-";
  }
  return Object.freeze({
    capturedAt,
    values: Object.freeze(values),
    displays: Object.freeze(displays)
  });
}

export function buildPerformanceRuns(
  history: readonly PerformancePoint[],
  metricId: string
): readonly (readonly PerformanceRunPoint[])[] {
  const runs: PerformanceRunPoint[][] = [];
  let current: PerformanceRunPoint[] = [];
  for (let index = 0; index < history.length; index += 1) {
    const value = history[index].values[metricId];
    if (typeof value === "number" && Number.isFinite(value)) {
      current.push(Object.freeze({ index, value }));
      continue;
    }
    if (current.length > 0) {
      runs.push(current);
      current = [];
    }
  }
  if (current.length > 0) {
    runs.push(current);
  }
  return Object.freeze(runs.map((run) => Object.freeze(run)));
}
