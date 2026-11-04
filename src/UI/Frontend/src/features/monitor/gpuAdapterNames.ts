import type { MetricFieldCatalogDevice } from "../../data/metricFields/metricFieldSource.ts";
import type { MetricDefinition } from "../../types.ts";

/** Resolve GPU names from catalog device identities while preserving backend indices. Omit missing or conflicting names rather than guessing. */
export function gpuAdapterNames(
  metrics: readonly Pick<MetricDefinition, "id" | "scopeKind" | "scopeKey">[],
  devices: readonly Pick<MetricFieldCatalogDevice, "deviceIdentity" | "kind" | "displayName">[] | null
): ReadonlyMap<string, string> {
  const nameByIdentity = new Map<string, string>();
  for (const device of devices ?? []) {
    const name = device.displayName?.trim();
    if (device.kind === "gpu_adapter" && name) nameByIdentity.set(device.deviceIdentity, name);
  }
  const names = new Map<string, string>();
  const conflicting = new Set<string>();
  for (const metric of metrics) {
    const index = gpuIndexOfMetric(metric.id);
    const name = index !== null && metric.scopeKind === "gpu" && metric.scopeKey ? nameByIdentity.get(metric.scopeKey) : undefined;
    if (index === null || name === undefined || conflicting.has(index)) continue;
    const known = names.get(index);
    if (known === undefined) names.set(index, name);
    else if (known !== name) { names.delete(index); conflicting.add(index); }
  }
  return names;
}

/** Return the unchanged GPU index string, or null for other metrics. */
export function gpuIndexOfMetric(metricId: string): string | null {
  return /^gpu\.(\d+)\./.exec(metricId)?.[1] ?? null;
}

/** Return the GPU name, or null when unavailable or unindexed. */
export function gpuNameForMetric(metricId: string, names: ReadonlyMap<string, string>): string | null {
  const index = gpuIndexOfMetric(metricId);
  return index === null ? null : names.get(index) ?? null;
}
