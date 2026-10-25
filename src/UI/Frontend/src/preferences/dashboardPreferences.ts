import { ResourceManager } from "./generated/frontendPoints.values.ts";
import { frontendPreferencesClient, type FrontendPreferencesClient } from "./frontendPreferences.ts";
import type { DashboardMetricBinding, DashboardSettings, DashboardSettingsResult, MetricDefinition, ResourceTableColumnSettings } from "../types.ts";

export const dashboardSettingsVersion = 16;
const points = ResourceManager.FrontendPoints;

function gpuIndex(id: string): number | null {
  const match = /^gpu\.(\d+)\./i.exec(id);
  return match ? Number(match[1]) : null;
}

export function dashboardMetricAvailable(id: string, binding: DashboardMetricBinding | null | undefined, catalog: readonly MetricDefinition[]): boolean {
  return binding?.scopeKind !== "gpu" || gpuIndex(id) === null
    || catalog.some(metric => metric.id === id && metric.scopeKind === "gpu" && metric.scopeKey === binding.scopeKey);
}

function validateDashboardCache(value: DashboardSettings): void {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("Invalid frontend dashboard cache");
  const limits = points.DashboardLayoutLimits;
  for (const name of ["cards", "resourceBars", "resourceTableColumns", "resourceTableProcessColumns"] as const) {
    const items = value[name];
    if (items == null) continue;
    if (!Array.isArray(items) || items.length > 10000) throw new Error("Invalid frontend dashboard cache");
    const ids = new Set<string>();
    for (const item of items) {
      if (!item || typeof item.id !== "string" || !item.id.trim() || ids.has(item.id)) throw new Error("Invalid frontend dashboard cache");
      ids.add(item.id);
      if (name === "cards") {
        const card = item as DashboardSettings["cards"][number];
        if (card.main != null && typeof card.main !== "string" || card.small != null && (!Array.isArray(card.small)
          || card.small.length > limits.smallMetricLimit || card.small.some(id => typeof id !== "string" || !id.trim()))) throw new Error("Invalid frontend dashboard cache");
      } else if (name === "resourceBars") {
        const bar = item as DashboardSettings["resourceBars"][number];
        if (typeof bar.metricId !== "string" || !bar.metricId.trim() || !["capacity", "active"].includes(bar.scaleMode)) throw new Error("Invalid frontend dashboard cache");
      } else {
        const column = item as ResourceTableColumnSettings;
        if (typeof column.visible !== "boolean" || column.width != null && (!Number.isFinite(column.width)
          || column.width < limits.minimumColumnWidth || column.width > limits.maximumColumnWidth)) throw new Error("Invalid frontend dashboard cache");
        // 来源：null／没有是自动选择，否则是非空的后端进程字段名。
        if (column.source != null && (typeof column.source !== "string" || !column.source.trim())) throw new Error("Invalid frontend dashboard cache");
      }
    }
  }
}

type ColumnSet = "software" | "process";

// 点值模板按模板 ID 给出默认列宽，每个列集合用自己的模板；GPU 列的模板 ID 把设备序号换回 {index}。
const defaultColumnWidths: Record<ColumnSet, ReadonlyMap<string, number>> = {
  software: new Map(points.DashboardLayoutDefaults.softwareColumns.map(template => [template.id, template.width])),
  process: new Map(points.DashboardLayoutDefaults.processColumns.map(template => [template.id, template.width]))
};

function columnTemplateId(id: string): string {
  return gpuIndex(id) === null ? id : id.replace(/^gpu\.\d+\./i, "gpu.{index}.");
}

function columnValues(columns: ResourceTableColumnSettings[], set: ColumnSet): ResourceTableColumnSettings[] {
  return columns.map(column => ({ ...column, width: column.width ?? defaultColumnWidths[set].get(columnTemplateId(column.id))
    ?? points.DashboardLayoutLimits.defaultColumnWidth }));
}

export function dashboardPointValues(catalog?: readonly MetricDefinition[]): DashboardSettings {
  const defaults = points.DashboardLayoutDefaults;
  const available = new Set(catalog === undefined ? defaults.builtInMetricIds
    : catalog.filter(metric => metric.selectable !== false).map(metric => metric.id));
  const indexes = [...new Set([...available].map(gpuIndex).filter((index): index is number => index !== null))].sort((a, b) => a - b);
  const expand = (value: string, index: number | null) => index === null ? value : value.replaceAll("{index}", String(index));
  const binding = (id: string): DashboardMetricBinding | null => {
    const metric = catalog?.find(metric => metric.id === id);
    return metric?.scopeKind === "gpu" && metric.scopeKey ? { scopeKind: "gpu", scopeKey: metric.scopeKey } : null;
  };
  const cards = defaults.cards.flatMap(template => (template.scope === "gpu" ? indexes : [null]).flatMap(index => {
    const main = expand(template.main, index);
    if (!template.required && !available.has(main)) return [];
    const small = template.small.map(id => expand(id, index)).filter(id => available.has(id));
    const mainBinding = binding(main);
    return [{ id: expand(template.id, index), main, small, mainBinding,
      smallBindings: template.scope === "gpu" ? small.map(id => binding(id)) : null }];
  }));
  const resourceBars = defaults.resourceBars.flatMap(template => (template.scope === "gpu" ? indexes : [null]).flatMap(index => {
    const metricId = expand(template.metricId, index);
    return !template.required && !available.has(metricId) ? [] : [{ id: expand(template.id, index), metricId,
      scaleMode: template.scaleMode, binding: binding(metricId) }];
  }));
  // Device columns are ordered by device and then field, as the original owner.
  const columns = (templates: typeof defaults.softwareColumns): ResourceTableColumnSettings[] => {
    const result: ResourceTableColumnSettings[] = [];
    let expandedGpu = false;
    for (const template of templates) {
      if (template.scope === "gpu") {
        if (expandedGpu) continue;
        expandedGpu = true;
        for (const index of indexes) for (const gpu of templates.filter(item => item.scope === "gpu")) {
          const id = expand(gpu.id, index);
          if (available.has(id)) result.push({ id, visible: gpu.visible, width: gpu.width, binding: binding(id) });
        }
      } else result.push({ id: template.id, visible: template.visible, width: template.width, binding: null });
    }
    return result;
  };
  // Bars have the same per-device ordering as columns.
  const gpuBars = resourceBars.filter(bar => gpuIndex(bar.metricId) !== null).sort((a, b) =>
    gpuIndex(a.metricId)! - gpuIndex(b.metricId)! || Number(a.metricId.endsWith(".vram")) - Number(b.metricId.endsWith(".vram")));
  let nextGpu = 0;
  return { version: dashboardSettingsVersion, usesFactoryDefaults: true, cards,
    resourceBars: resourceBars.map(bar => gpuIndex(bar.metricId) === null ? bar : gpuBars[nextGpu++]!),
    resourceTableColumns: columns(defaults.softwareColumns), resourceTableProcessColumns: columns(defaults.processColumns) };
}

/** Resolve stable device identities only for display/demand; cache data is kept intact. */
export function resolveDashboardBindings(settings: DashboardSettings, catalog: readonly MetricDefinition[]): DashboardSettings {
  const resolve = (id: string | null | undefined, binding: DashboardMetricBinding | null | undefined) => {
    if (!id || binding?.scopeKind !== "gpu" || gpuIndex(id) === null) return id;
    const device = catalog.find(metric => metric.scopeKind === "gpu" && metric.scopeKey === binding.scopeKey && gpuIndex(metric.id) !== null);
    // A missing stable device must not borrow whichever GPU now has its old index.
    return device ? id.replace(/^gpu\.\d+\./i, `gpu.${gpuIndex(device.id)}.`) : id;
  };
  return { ...settings,
    cards: settings.cards.map(card => ({ ...card, main: resolve(card.main, card.mainBinding),
      small: (card.small ?? []).map((id, index) => resolve(id, card.smallBindings?.[index])!) })),
    resourceBars: settings.resourceBars.map(bar => ({ ...bar, metricId: resolve(bar.metricId, bar.binding)! })),
    resourceTableColumns: settings.resourceTableColumns.map(column => ({ ...column, id: resolve(column.id, column.binding)! })),
    resourceTableProcessColumns: settings.resourceTableProcessColumns.map(column => ({ ...column, id: resolve(column.id, column.binding)! }))
  };
}

export function dashboardCacheResult(settings: DashboardSettings): DashboardSettingsResult {
  return { settings, updatedAt: new Date().toISOString(),
    source: { kind: settings.usesFactoryDefaults ? "pointValues" : "frontendCache", sourceVersion: settings.version } };
}

export async function readDashboardPreferences(catalog?: readonly MetricDefinition[], client: FrontendPreferencesClient = frontendPreferencesClient()): Promise<DashboardSettingsResult> {
  const preferences = await client.read();
  const cached = preferences.dashboard;
  if (cached == null) return dashboardCacheResult(dashboardPointValues(catalog));
  validateDashboardCache(cached);
  let defaults: DashboardSettings | undefined;
  const pointValues = () => defaults ??= dashboardPointValues(catalog);
  // Missing entries in an older frontend/legacy cache use the point's entry;
  // explicit empty arrays remain empty.
  return dashboardCacheResult(resolveDashboardBindings({ ...cached, cards: cached.cards ?? pointValues().cards,
    resourceBars: cached.resourceBars ?? pointValues().resourceBars,
    resourceTableColumns: columnValues(cached.resourceTableColumns ?? pointValues().resourceTableColumns, "software"),
    resourceTableProcessColumns: columnValues(cached.resourceTableProcessColumns ?? pointValues().resourceTableProcessColumns, "process"),
    usesFactoryDefaults: false }, catalog ?? []));
}

export async function saveDashboardPreferences(settings: DashboardSettings, client: FrontendPreferencesClient = frontendPreferencesClient()): Promise<DashboardSettingsResult> {
  const current = await client.read();
  validateDashboardCache(settings);
  const dashboard = { ...structuredClone(settings), usesFactoryDefaults: false };
  await client.save({ ...current, dashboard }, "dashboard");
  return dashboardCacheResult(dashboard);
}

export async function resetDashboardPreferences(client: FrontendPreferencesClient = frontendPreferencesClient()): Promise<void> {
  await client.save({ ...await client.read(), dashboard: null }, "dashboard");
}
