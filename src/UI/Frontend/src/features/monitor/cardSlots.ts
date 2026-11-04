import { ResourceManager } from "../../preferences/generated/frontendPoints.values.ts";
import type {
  DashboardCardSlot,
  DashboardMetricBinding,
  DashboardSlotSize,
  LegacyDashboardCardSettings
} from "../../types.ts";

/* Slot units must fit cardUnits. Resizing credits the current slot; cross-card moves require space. Preserve migrated oversized cards and allow only reductions. */

const limits = ResourceManager.FrontendPoints.DashboardLayoutLimits;

export const cardUnits = limits.cardUnits;
export const dashboardSlotSizes: readonly DashboardSlotSize[] = ["small", "medium", "large", "full"];

export function slotUnits(size: DashboardSlotSize): number {
  switch (size) {
    case "small": return 1;
    case "medium": return 2;
    case "large": return 3;
    case "full": return cardUnits;
  }
}

/** Cached layouts may exceed editable capacity after migration. */
export const cardUnitLimit = Math.max(cardUnits, slotUnits("medium") + limits.smallMetricLimit);

export function isDashboardSlotSize(value: unknown): value is DashboardSlotSize {
  return typeof value === "string" && (dashboardSlotSizes as readonly string[]).includes(value);
}

export function usedUnits(slots: readonly Pick<DashboardCardSlot, "size">[]): number {
  let units = 0;
  for (const slot of slots) units += slotUnits(slot.size);
  return units;
}

export function remainingUnits(slots: readonly Pick<DashboardCardSlot, "size">[]): number {
  return Math.max(0, cardUnits - usedUnits(slots));
}

/** Return sizes that fit, crediting the resized slot when supplied. */
export function sizesThatFit(slots: readonly Pick<DashboardCardSlot, "size">[], resizing?: number): DashboardSlotSize[] {
  const own = resizing === undefined ? 0 : slotUnits(slots[resizing].size);
  const available = remainingUnits(slots) + own;
  return dashboardSlotSizes.filter((size) => slotUnits(size) <= available);
}

/** Migrate the main metric to a medium slot and nonempty rows to minimum slots. */
export function legacyCardSlots(card: LegacyDashboardCardSettings): DashboardCardSlot[] {
  const slots: DashboardCardSlot[] = [];
  const add = (metricId: string | null | undefined, binding: DashboardMetricBinding | null | undefined, size: DashboardSlotSize) => {
    const id = typeof metricId === "string" ? metricId.trim() : "";
    if (id) slots.push({ metricId: id, binding: binding ?? null, size });
  };
  add(card.main, card.mainBinding, "medium");
  (card.small ?? []).forEach((metricId, index) => add(metricId, card.smallBindings?.[index], "small"));
  return slots;
}
