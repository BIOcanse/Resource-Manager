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
import { textOrEmpty } from "../utils";

export const dashboardSettingsVersion = 15;

export const defaultCards: DashboardCardSettings[] = [
  { id: "cpu", main: "cpu.usage", small: ["cpu.frequency", "cpu.frequencyPercent"] },
  { id: "memory", main: "memory.usage", small: ["memory.percent"] }
];

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
      for (let index = 0; index < (card.small ?? []).length && small.length < 3; index += 1) {
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

  return normalized.length > 0 ? normalized : structuredClone(defaultCards);
}

export function metricBindingForDefinition(
  metric: MetricDefinition | null | undefined
): DashboardMetricBinding | null {
  return metric?.scopeKind === "gpu" && textOrEmpty(metric.scopeKey)
    ? { scopeKind: "gpu", scopeKey: metric.scopeKey!.trim() }
    : null;
}

function normalizeMetricBinding(
  metricId: string | null | undefined,
  binding: DashboardMetricBinding | null | undefined
): DashboardMetricBinding | null {
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

  return normalized.length > 0 ? normalized : defaultResourceBars([]);
}

export function normalizeResourceTableColumns(
  candidateColumns?: ResourceTableColumnSettings[] | null,
  metricCatalog: MetricDefinition[] = [],
  mode: ResourceTableViewMode = "software")
: ResourceTableColumnSettings[] {
  const known = defaultResourceTableColumns(metricCatalog, mode);
  const byId = new Map(known.map((column) => [column.id, column]));
  const normalized = (candidateColumns ?? [])
    .filter((column) => byId.has(textOrEmpty(column.id)))
    .map((column) => ({
      id: column.id.trim(),
      visible: column.id.trim() === "name" ? true : Boolean(column.visible),
      width: clampResourceTableColumnWidth(column.width ?? byId.get(column.id.trim())?.width ?? 100),
      binding: normalizeMetricBinding(column.id, column.binding)
    }));
  const seen = new Set(normalized.map((column) => column.id));
  for (const column of known) {
    if (!seen.has(column.id)) {
      normalized.push({
        id: column.id,
        visible: column.visible,
        width: clampResourceTableColumnWidth(column.width ?? 100),
        binding: column.binding ?? null
      });
    }
  }

  return normalized;
}

export function defaultResourceBars(catalog: MetricDefinition[]): ResourceBarSettings[] {
  const ids = new Set(catalog.map((metric) => metric.id));
  const preferred = [
    "cpu.usage",
    "memory.usage",
    "virtualMemory.usage",
    ...catalog
      .map((metric) => metric.id)
      .filter((id) => /^gpu\.\d+\.(usage|vram)$/i.test(id))
      .sort((left, right) => left.localeCompare(right, undefined, { numeric: true }))
  ];
  return preferred
    .filter((id) => ids.size === 0 || ids.has(id))
    .map((metricId) => ({
      id: `resource-${metricId.replaceAll(".", "-")}`,
      metricId,
      scaleMode: "capacity" as const,
      binding: metricBindingForDefinition(catalog.find((metric) => metric.id === metricId))
    }));
}

export function defaultResourceTableColumns(metricCatalog: MetricDefinition[], mode: ResourceTableViewMode = "software"): ResourceTableColumnSettings[] {
  const processMode = mode === "process";
  return [
    { id: "name", visible: true, width: 260 },
    ...(processMode ? [{ id: "pid", visible: true, width: 76 }] : []),
    { id: "status", visible: true, width: 92 },
    ...(processMode ? [
      { id: "user", visible: true, width: 150 },
      { id: "architecture", visible: true, width: 76 }
    ] : []),
    { id: "cpu", visible: true, width: 86 },
    { id: "memory", visible: true, width: 110 },
    ...metricCatalog
      .map((metric) => metric.id)
      .filter((id) => /^gpu\.\d+\.(usage|vram)$/i.test(id))
      .sort((left, right) => resourceTableGpuColumnOrder(left) - resourceTableGpuColumnOrder(right))
      .map((id) => ({
        id,
        visible: true,
        width: id.endsWith(".vram") ? 132 : 108,
        binding: metricBindingForDefinition(metricCatalog.find((metric) => metric.id === id))
      })),
    { id: "disk", visible: true, width: 106 },
    { id: "network", visible: true, width: 106 }
  ];
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
  return Math.round(Math.min(520, Math.max(56, Number.isFinite(numeric) ? numeric : 100)));
}

export function isProcessDetailResourceTableColumn(columnId: string) {
  return columnId === "pid" || columnId === "user" || columnId === "architecture";
}

function resourceTableGpuColumnOrder(metricId: string) {
  const match = /^gpu\.(\d+)\.(usage|vram)$/i.exec(metricId);
  return match ? Number(match[1]) * 2 + (match[2].toLowerCase() === "vram" ? 1 : 0) : 999;
}
