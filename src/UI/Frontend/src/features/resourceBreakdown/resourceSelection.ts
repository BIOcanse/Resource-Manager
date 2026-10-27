import type { ResourcePrecisionSelection } from "./ResourceBreakdown.tsx";
import type { ResourceBreakdownBar } from "../../types.ts";

/**
 * 条上能选的段，按条上的顺序：值大于 0 的软件段，含空余时再加最后一段空余（容量减总量，大于 0 才有）。
 * 资源条画哪些段、键盘逐段走、数据更新后保留选择，都按这一份。
 */
export function selectableResourceSegmentIds(bar: ResourceBreakdownBar): string[] {
  const ids = (bar.software ?? [])
    .filter((segment) => segment?.softwareId && Number(segment.value) > 0)
    .map((segment) => segment.softwareId);
  if (resourceEmptyCapacityValue(bar) > 0) {
    ids.push(resourceEmptySegmentId(bar.metricId));
  }
  return ids;
}

/** 空余段的标识：前端自己造的段，不会和后端的软件标识重名。 */
export function resourceEmptySegmentId(metricId: string) {
  return `__resource-empty:${metricId}`;
}

/** 含空余时的空余量（容量减总量）；现有占用模式、容量未知或已满时为 0。 */
export function resourceEmptyCapacityValue(bar: ResourceBreakdownBar) {
  const capacity = Number(bar.capacityValue) || 0;
  const value = bar.scaleMode !== "active" && capacity > 0
    ? Math.max(0, capacity - Number(bar.totalValue))
    : 0;
  return Number.isFinite(value) ? value : 0;
}

/**
 * 点一段（或在条上按 Enter/空格）之后的选择：点的正是选中的那一段就取消选中、收起它的明细，否则选中点的这一段。
 * 软件段、系统/驱动保留和空余都一样；同时只有一段选中，展开的明细就是它（见 monitorStore 的 resourceSelection）。
 */
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
  return retainedIndex >= 0 ? { ...current, index: retainedIndex } : null;
}
