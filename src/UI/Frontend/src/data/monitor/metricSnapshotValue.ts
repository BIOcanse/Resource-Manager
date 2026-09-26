import type { MetricSnapshot } from "../../types.ts";

export function metricSnapshotObservedAt(
  snapshot: MetricSnapshot,
  _metricId?: string
): string | null {
  return snapshot.capturedAt;
}
