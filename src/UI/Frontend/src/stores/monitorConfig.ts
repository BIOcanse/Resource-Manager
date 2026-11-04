import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";
import { dashboardPointValues } from "../preferences/dashboardPreferences.ts";
export { dashboardSettingsVersion } from "../preferences/dashboardPreferences.ts";
import type { DashboardSlotRef } from "../features/monitor/Dashboard";
import type {
  DashboardCardSettings,
  DashboardCardSlot,
  DashboardMetricBinding,
  DashboardSlotSize,
  MetricDefinition,
  ResourceBarSettings,
  ResourceTableColumnSettings,
  ResourceTableViewMode
} from "../types";
import { normalizeResourceBarScaleMode } from "../features/resourceBreakdown/resourceBarScaleCapabilities.ts";
import { deviceFieldScopeKind, fieldCardSlot } from "../features/monitor/fieldCards.ts";
import { isDashboardSlotSize, remainingUnits, sizesThatFit, slotUnits } from "../features/monitor/cardSlots.ts";
import { textOrEmpty } from "../utils.ts";

const limits = ResourceManager.FrontendPoints.DashboardLayoutLimits;
export const defaultCards: DashboardCardSettings[] = dashboardPointValues().cards;
/* Card edits return a new list, or the original list when the target is missing or capacity is insufficient. */

function updateCard(cards: readonly DashboardCardSettings[], cardId: string, change: (slots: DashboardCardSlot[]) => DashboardCardSlot[] | null) {
  const index = cards.findIndex((card) => card.id === cardId);
  if (index < 0) return cards as DashboardCardSettings[];
  const slots = change([...cards[index].slots]);
  if (!slots) return cards as DashboardCardSettings[];
  const next = [...cards];
  next[index] = { ...cards[index], slots };
  return next;
}

/** Append a slot only if its size fits the remaining capacity. */
export function addCardSlot(cards: readonly DashboardCardSettings[], cardId: string, slot: DashboardCardSlot) {
  return updateCard(cards, cardId, (slots) => sizesThatFit(slots).includes(slot.size) ? [...slots, slot] : null);
}

/** Replace the metric without changing slot size. */
export function replaceCardSlotMetric(cards: readonly DashboardCardSettings[], ref: DashboardSlotRef, metricId: string, binding: DashboardMetricBinding | null) {
  return updateCard(cards, ref.cardId, (slots) => {
    if (!slots[ref.index]) return null;
    slots[ref.index] = { ...slots[ref.index], metricId, binding };
    return slots;
  });
}

/** Resize within the capacity remaining after releasing this slot. */
export function resizeCardSlot(cards: readonly DashboardCardSettings[], ref: DashboardSlotRef, size: DashboardSlotSize) {
  return updateCard(cards, ref.cardId, (slots) => {
    if (!slots[ref.index] || slots[ref.index].size === size || !sizesThatFit(slots, ref.index).includes(size)) return null;
    slots[ref.index] = { ...slots[ref.index], size };
    return slots;
  });
}

export function removeCardSlot(cards: readonly DashboardCardSettings[], ref: DashboardSlotRef) {
  return updateCard(cards, ref.cardId, (slots) => slots[ref.index] ? slots.filter((_, index) => index !== ref.index) : null);
}

/** Reorder within a card, or insert before a slot in another card if capacity allows. */
export function moveCardSlot(cards: readonly DashboardCardSettings[], source: DashboardSlotRef, target: DashboardSlotRef) {
  const from = cards.find((card) => card.id === source.cardId);
  const to = cards.find((card) => card.id === target.cardId);
  const moving = from?.slots[source.index];
  if (!from || !to || !moving || (source.cardId === target.cardId && source.index === target.index)) {
    return cards as DashboardCardSettings[];
  }
  if (from === to) {
    return updateCard(cards, from.id, (slots) => {
      slots.splice(source.index, 1);
      slots.splice(Math.min(target.index, slots.length), 0, moving);
      return slots;
    });
  }
  if (remainingUnits(to.slots) < slotUnits(moving.size)) {
    return cards as DashboardCardSettings[];
  }
  const withoutSource = removeCardSlot(cards, source);
  return updateCard(withoutSource, to.id, (slots) => {
    slots.splice(Math.min(target.index, slots.length), 0, moving);
    return slots;
  });
}

/** Drop invalid slots and empty cards; preserve migrated cards even when their total size exceeds capacity. */
export function normalizeCards(candidateCards?: DashboardCardSettings[] | null): DashboardCardSettings[] {
  return (candidateCards ?? [])
    .filter(Boolean)
    .map((card) => ({
      id: textOrEmpty(card.id) || crypto.randomUUID(),
      slots: (card.slots ?? []).flatMap((slot) => {
        const metricId = textOrEmpty(slot?.metricId);
        return metricId && isDashboardSlotSize(slot.size)
          ? [{ metricId, size: slot.size, binding: normalizeMetricBinding(metricId, slot.binding) }]
          : [];
      })
    }))
    .filter((card) => card.slots.length > 0);
}

export function metricBindingForDefinition(
  metric: MetricDefinition | null | undefined
): DashboardMetricBinding | null {
  if (metric?.scopeKind === deviceFieldScopeKind && metric.scopeKey) {
    // Device identities are opaque; preserve them without trimming.
    return { scopeKind: deviceFieldScopeKind, scopeKey: metric.scopeKey };
  }
  return metric?.scopeKind === "gpu" && textOrEmpty(metric.scopeKey)
    ? { scopeKind: "gpu", scopeKey: metric.scopeKey!.trim() }
    : null;
}

function normalizeMetricBinding(
  metricId: string | null | undefined,
  binding: DashboardMetricBinding | null | undefined
): DashboardMetricBinding | null {
  if (fieldCardSlot(metricId, binding)) {
    return { scopeKind: deviceFieldScopeKind, scopeKey: binding!.scopeKey };
  }
  return /^gpu\.\d+\./i.test(textOrEmpty(metricId))
    && binding?.scopeKind === "gpu"
    && textOrEmpty(binding.scopeKey)
    ? { scopeKind: "gpu", scopeKey: binding.scopeKey.trim() }
    : null;
}

export function normalizeResourceBars(candidateBars?: ResourceBarSettings[] | null): ResourceBarSettings[] {
  const normalized = (candidateBars ?? [])
    .filter((bar) => textOrEmpty(bar.metricId))
    .map((bar) => ({
      id: textOrEmpty(bar.id) || `resource-${bar.metricId.replaceAll(".", "-")}`,
      metricId: bar.metricId.trim(),
      scaleMode: normalizeResourceBarScaleMode(bar.metricId.trim(), bar.scaleMode),
      binding: normalizeMetricBinding(bar.metricId, bar.binding),
      // Preserve the source; null selects automatically.
      source: bar.source ?? null
    }));

  return normalized;
}

export function normalizeResourceTableColumns(
  candidateColumns?: ResourceTableColumnSettings[] | null,
  metricCatalog: MetricDefinition[] = [],
  mode: ResourceTableViewMode = "software")
: ResourceTableColumnSettings[] {
  const known = defaultResourceTableColumns(metricCatalog, mode);
  if (candidateColumns == null) return known;
  const byId = new Map(known.map((column) => [column.id, column]));
  const normalized = (candidateColumns ?? [])
    .filter((column) => textOrEmpty(column.id))
    .map((column) => ({
      id: column.id.trim(),
      visible: Boolean(column.visible),
      width: clampResourceTableColumnWidth(column.width ?? byId.get(column.id.trim())?.width ?? limits.defaultColumnWidth),
      binding: normalizeMetricBinding(column.id, column.binding),
      // Preserve the source; null selects automatically.
      source: column.source ?? null
    }));
  return normalized;
}

export function defaultResourceBars(catalog: MetricDefinition[]): ResourceBarSettings[] {
  return dashboardPointValues(catalog).resourceBars;
}

export function defaultResourceTableColumns(catalog: MetricDefinition[], mode: ResourceTableViewMode = "software"): ResourceTableColumnSettings[] {
  const settings = dashboardPointValues(catalog);
  return mode === "process" ? settings.resourceTableProcessColumns : settings.resourceTableColumns;
}
export function allCatalogMetricIds(metricCatalog: MetricDefinition[]) {
  return metricCatalog.map((metric) => metric.id).filter(Boolean);
}

export function allResourceSampleMetricIds(metricCatalog: MetricDefinition[]) {
  const ids = new Set([
    "cpu.usage",
    "memory.usage",
    "virtualMemory.usage",
    "disk.io",
    "disk.read",
    "disk.write",
    "network.traffic",
    "network.receive",
    "network.send",
    "network.raw.traffic",
    "network.raw.receive",
    "network.raw.send"
  ]);
  for (const metric of metricCatalog) {
    if (/^gpu\.\d+\.(usage|vram)$/i.test(metric.id)) {
      ids.add(metric.id);
    }
  }

  return [...ids];
}

export function clampResourceTableColumnWidth(width: unknown) {
  const numeric = Number(width);
  return Math.round(Math.min(limits.maximumColumnWidth, Math.max(limits.minimumColumnWidth, Number.isFinite(numeric) ? numeric : limits.defaultColumnWidth)));
}

export function isProcessDetailResourceTableColumn(columnId: string) {
  return columnId === "pid" || columnId === "user" || columnId === "architecture";
}
