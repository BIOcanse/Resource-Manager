import { ResourceManager } from "../../preferences/generated/frontendPoints.values.ts";
import type {
  DashboardCardSlot,
  DashboardMetricBinding,
  DashboardSlotSize,
  LegacyDashboardCardSettings
} from "../../types.ts";

/*
 * 监视卡片的槽位（用户 2026-10-25：「让这里能够自由选择大小分布……一个卡片一共六个单位空间。选择大小后就可以在里面选择监控项」）。
 *
 * 一张卡片纵向共 cardUnits（6）个单位，里面从上到下一列槽位。每个槽位放一个监控项，有一个大小：
 * 最小 1 个单位、中 2、大 3、超大占满整张卡片。剩余 = 6 − 各槽位单位之和（不小于 0）。
 * - 新增：剩余至少 1 时卡片末尾有“+”；只列单位不超过剩余的大小，选好大小再选监控项，确认后加在末尾。
 * - 改大小：只列单位不超过“剩余 + 这个槽位自己的单位”的大小。
 * - 换监控项、删除、同卡片里拖动换位置都不改单位之和；拖到别的卡片要那张卡片的剩余放得下。
 * - 旧布局（一个主读数加若干行）读入时主读数是“中”放最前，每行是“最小”依次往后，绑定跟着走。
 *   旧布局最多一个主读数加 smallMetricLimit 行，可能超过 6 个单位：原样保留，这张卡片没有剩余，只能改小。
 */

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

/** 缓存里一张卡片最多的单位：编辑不超过 cardUnits；旧布局迁移来的卡片最多一个主读数（中）加 smallMetricLimit 行。 */
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

/**
 * 放得下的大小，从小到大。新增时不给 resizing；给某个槽位改大小时传它的序号，它自己占的单位算作可用。
 */
export function sizesThatFit(slots: readonly Pick<DashboardCardSlot, "size">[], resizing?: number): DashboardSlotSize[] {
  const own = resizing === undefined ? 0 : slotUnits(slots[resizing].size);
  const available = remainingUnits(slots) + own;
  return dashboardSlotSizes.filter((size) => slotUnits(size) <= available);
}

/** 旧布局转成槽位：主读数是“中”放在最前，每行是“最小”依次放后面；空的项不要。 */
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
