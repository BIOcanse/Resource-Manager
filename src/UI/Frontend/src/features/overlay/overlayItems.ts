import type { PerformanceOverlayEditingConstraints, PerformanceOverlayItem } from "../../types.ts";
import { ResourceManager } from "../../preferences/generated/desktopPoints.values.ts";
import { itemListIssue, type ItemListIssue } from "../itemSelection/itemSelectionModel.ts";

/* The backend owns overlay items and defaults; array order is drawing order and save validation matches the backend. */

/** Blank nonframe intervals use the desktop overlay subscription interval; individual items may override it. */
export const overlayDefaultIntervalMs = ResourceManager.Desktop.DesktopPoints.overlay_sample_interval_ms;

/** Return null when the item group is valid. */
export function overlayItemsIssue(items: readonly PerformanceOverlayItem[], constraints: Pick<PerformanceOverlayEditingConstraints, "maximumItems">): ItemListIssue | null {
  return itemListIssue(items, constraints.maximumItems);
}
