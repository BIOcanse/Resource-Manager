import type { PushValueItemPolicy } from "../../frontendRuntime/push/PushCurrentItems.ts";
import type { GpuSpecializedTelemetryAdapter, GpuSpecializedTelemetrySnapshot } from "../../types.ts";
import type { GpuSpecializedTelemetryQuery } from "./gpuSchedulingModelSource.ts";

export const gpuSpecializedCurrentItems: PushValueItemPolicy<GpuSpecializedTelemetryQuery, GpuSpecializedTelemetrySnapshot> = {
  absentMeansEmpty: () => true,
  includes: (query, key) => query.counterIds.includes((JSON.parse(key) as [number, string])[1]),
  isPending: (query, keys) => query.counterIds.some(id => !keys.has(JSON.stringify([null, id]))),
  split: (query, value) => [...value.adapters.flatMap(adapter => adapter.counters.map(counter => ({
    key: JSON.stringify([adapter.adapterIndex, counter.counterId]), capturedAt: value.capturedAt,
    value: { ...value, adapters: [{ ...adapter, counters: [counter] }] }
  }))), ...query.counterIds.map(id => ({ key: JSON.stringify([null, id]), capturedAt: value.capturedAt,
    value: { ...value, adapters: [] } }))],
  combine: (_query, values, latest) => {
    const adapters = new Map<number, GpuSpecializedTelemetryAdapter>();
    for (const value of values) for (const adapter of value.adapters) {
      const previous = adapters.get(adapter.adapterIndex);
      adapters.set(adapter.adapterIndex, { ...adapter, counters: [...previous?.counters ?? [], ...adapter.counters] });
    }
    return { ...latest, adapters: [...adapters.values()] };
  }
};
