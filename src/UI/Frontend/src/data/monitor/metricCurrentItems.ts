import type { PushValueItemPolicy } from "../../frontendRuntime/push/PushCurrentItems.ts";
import type { MetricSnapshot } from "../../types.ts";
import type { MetricSnapshotQuery } from "./monitorSourcesApi.ts";

// Dictionary IDs are backend metric slots. A device/source change replaces that slot.
export const metricCurrentItems: PushValueItemPolicy<MetricSnapshotQuery, MetricSnapshot> = {
  // MetricSnapshotWireSnapshot sends real status-bearing placeholders for every requested ID.
  absentMeansEmpty: () => false,
  includes: (query, key) => query.ids === null || query.ids.includes(key),
  // IDs control sampling demand. Offscreen dashboard slots still display their current values.
  includesInProjection: () => true,
  isPending: (query, keys) => query.ids?.some(id => !keys.has(id)) ?? false,
  split: (_query, value) => Object.entries(value.items).map(([key, item]) => ({
    key, capturedAt: item.observedAt ?? value.capturedAt,
    value: { ...value, items: { [key]: item } }
  })),
  combine: (_query, values, latest) => ({ ...latest, items: Object.assign({}, ...values.map(value => value.items)) })
};
