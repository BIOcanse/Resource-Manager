import type { ResourceScaleMode } from "../types";

const capacityMetricIds = new Set([
  "cpu.usage",
  "memory.usage",
  "virtualmemory.usage"
]);

export function resourceBarSupportsCapacity(metricId: string) {
  return capacityMetricIds.has(metricId.trim().toLowerCase())
    || /^gpu\.\d+\.(usage|vram)$/i.test(metricId);
}

export function normalizeResourceBarScaleMode(metricId: string, scaleMode?: ResourceScaleMode): ResourceScaleMode {
  if (!resourceBarSupportsCapacity(metricId)) {
    return "active";
  }

  return scaleMode === "active" ? "active" : "capacity";
}
