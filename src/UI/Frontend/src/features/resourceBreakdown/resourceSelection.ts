import type { ResourcePrecisionSelection } from "./ResourceBreakdown.tsx";
import type { ResourceBreakdownBar } from "../../types.ts";

/** Share positive-segment order between rendering, keyboard navigation, and selection retention; append positive free space when enabled. */
export function selectableResourceSegmentIds(bar: ResourceBreakdownBar): string[] {
  const ids = (bar.software ?? [])
    .filter((segment) => segment?.softwareId && Number(segment.value) > 0)
    .map((segment) => segment.softwareId);
  if (resourceEmptyCapacityValue(bar) > 0) {
    ids.push(resourceEmptySegmentId(bar.metricId));
  }
  return ids;
}

/** Use a frontend-only free-space identity that cannot collide with backend software IDs. */
export function resourceEmptySegmentId(metricId: string) {
  return `__resource-empty:${metricId}`;
}

/** Return zero unless capacity mode has known positive free space. */
export function resourceEmptyCapacityValue(bar: ResourceBreakdownBar) {
  const capacity = Number(bar.capacityValue) || 0;
  const value = bar.scaleMode !== "active" && capacity > 0
    ? Math.max(0, capacity - Number(bar.totalValue))
    : 0;
  return Number.isFinite(value) ? value : 0;
}

/** Activating the selected segment clears it; otherwise select it. All segment kinds share one selection owner. */
export function toggledResourceSelection(
  current: ResourcePrecisionSelection | null,
  target: ResourcePrecisionSelection
): ResourcePrecisionSelection | null {
  return current !== null && current.metricId === target.metricId && current.softwareId === target.softwareId ? null : target;
}

export function reconcileResourceSelection(
  bars: readonly ResourceBreakdownBar[],
  current: ResourcePrecisionSelection | null
): ResourcePrecisionSelection | null {
  if (!current) {
    return null;
  }

  const currentBar = bars.find((bar) => bar.metricId === current.metricId);
  const retainedIndex = currentBar
    ? selectableResourceSegmentIds(currentBar).indexOf(current.softwareId)
    : -1;
  // Preserve unchanged selection identity so unrelated frames do not notify readers.
  return retainedIndex < 0 ? null : retainedIndex === current.index ? current : { ...current, index: retainedIndex };
}
