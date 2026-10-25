import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";
import { dashboardPointValues } from "../preferences/dashboardPreferences.ts";
export { dashboardSettingsVersion } from "../preferences/dashboardPreferences.ts";
import type { DashboardSlotRef } from "../features/monitor/Dashboard";
import type {
  DashboardCardSettings,
  DashboardMetricBinding,
  MetricDefinition,
  ResourceBarSettings,
  ResourceTableColumnSettings,
  ResourceTableViewMode
} from "../types";
import { normalizeResourceBarScaleMode } from "../features/resourceBreakdown/resourceBarScaleCapabilities";
import { deviceFieldScopeKind, fieldCardSlot } from "../features/monitor/fieldCards.ts";
import { textOrEmpty } from "../utils";

const limits = ResourceManager.FrontendPoints.DashboardLayoutLimits;
export const dashboardSmallMetricLimit = limits.smallMetricLimit;
export const defaultCards: DashboardCardSettings[] = dashboardPointValues().cards;
export function readDashboardSlot(card: DashboardCardSettings, slot: DashboardSlotRef) {
  if (slot.slot === "main") {
    return card.main ?? null;
  }

  return card.small?.[slot.index] ?? null;
}

export function readDashboardSlotBinding(
  card: DashboardCardSettings,
  slot: DashboardSlotRef
): DashboardMetricBinding | null {
  if (slot.slot === "main") {
    return card.mainBinding ?? null;
  }

  return card.smallBindings?.[slot.index] ?? null;
}

export function writeDashboardSlot(
  card: DashboardCardSettings,
  slot: DashboardSlotRef,
  metricId: string | null,
  binding: DashboardMetricBinding | null
) {
  if (slot.slot === "main") {
    card.main = metricId;
    card.mainBinding = metricId ? binding : null;
    return;
  }

  const small = [...(card.small ?? [])];
  const smallBindings = [...(card.smallBindings ?? [])];
  if (metricId) {
    small[slot.index] = metricId;
    smallBindings[slot.index] = binding;
  } else {
    small.splice(slot.index, 1);
    smallBindings.splice(slot.index, 1);
  }
  card.small = small;
  card.smallBindings = smallBindings;
}

export function normalizeCards(candidateCards?: DashboardCardSettings[] | null): DashboardCardSettings[] {
  const normalized = (candidateCards ?? [])
    .filter(Boolean)
    .map((card) => {
      const main = textOrEmpty(card.main) || null;
      const small: string[] = [];
      const smallBindings: Array<DashboardMetricBinding | null> = [];
      for (let index = 0; index < (card.small ?? []).length && small.length < dashboardSmallMetricLimit; index += 1) {
        const metricId = textOrEmpty(card.small[index]);
        if (!metricId) {
          continue;
        }
        small.push(metricId);
        smallBindings.push(normalizeMetricBinding(metricId, card.smallBindings?.[index]));
      }

      return {
        id: textOrEmpty(card.id) || crypto.randomUUID(),
        main,
        small,
        mainBinding: normalizeMetricBinding(main, card.mainBinding),
        smallBindings
      };
    })
    .filter((card) => Boolean(card.main) || card.small.length > 0);

  return normalized;
}

export function metricBindingForDefinition(
  metric: MetricDefinition | null | undefined
): DashboardMetricBinding | null {
  if (metric?.scopeKind === deviceFieldScopeKind && metric.scopeKey) {
    // 设备身份是不透明字符串，原样保存，不修剪。
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
      binding: normalizeMetricBinding(bar.metricId, bar.binding)
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
      // 来源原样保留（null 是自动选择），见 ResourceTableColumnSettings.source。
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
