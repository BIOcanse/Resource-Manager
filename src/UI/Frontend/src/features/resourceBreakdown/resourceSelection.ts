import type { ResourcePrecisionSelection } from "./ResourceBreakdown.tsx";
import type {
  ResourceBreakdownBar,
  ResourceSoftwareSegment
} from "../../types.ts";

export function reconcileResourceSelection(
  bars: readonly ResourceBreakdownBar[],
  current: ResourcePrecisionSelection | null
): ResourcePrecisionSelection | null {
  if (!current) {
    return null;
  }

  const currentBar = bars.find((bar) => bar.metricId === current.metricId);
  const currentSegments = selectableResourceSegments(currentBar);
  if (currentSegments.length === 0) {
    return null;
  }

  const retainedIndex = currentSegments.findIndex((segment) =>
    segment.softwareId === current.softwareId);
  if (retainedIndex >= 0) {
    return { ...current, index: retainedIndex };
  }

  return null;
}

export function selectableResourceSegments(
  bar?: ResourceBreakdownBar
): ResourceSoftwareSegment[] {
  return (bar?.software ?? []).filter((segment) =>
    Number.isFinite(segment.systemPercent) && segment.systemPercent >= 0);
}

