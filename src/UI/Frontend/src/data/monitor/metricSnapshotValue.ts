import type { MetricSnapshot } from "../../types.ts";

export function metricSnapshotObservedAt(
  snapshot: MetricSnapshot,
  metricId?: string
): string | null {
  if (!metricId) return snapshot.capturedAt;
  const metric = snapshot.items[metricId];
  return metric?.status === undefined ? snapshot.capturedAt
    : metric.status === 1 ? metric.observedAt ?? null : null;
}
