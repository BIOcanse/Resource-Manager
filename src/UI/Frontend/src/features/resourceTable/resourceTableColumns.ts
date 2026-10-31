import type { ProcessMetricField } from "../../data/resourceMonitor/processFieldCatalog.ts";
import type { MetricDefinition, ResourceTableColumnSettings, ResourceTableViewMode } from "../../types.ts";
import { ResourceManager } from "../../preferences/generated/frontendPoints.values.ts";
import { dashboardMetricAvailable, dashboardPointValues } from "../../preferences/dashboardPreferences.ts";
import { resourceTableColumnLabel } from "../../presentation/metricLabels.ts";
import { resourceTableColumnText, resourceTableMetricId } from "./resourceTableSources.ts";
import { uiText } from "../../text.ts";

/** Only backend-declared capabilities enter the shared selector. */
export function tableFieldCatalog(fields: readonly ProcessMetricField[], mode: ResourceTableViewMode, catalog: MetricDefinition[] = []): MetricDefinition[] {
  const eligible = fields.filter(field => mode !== "performance" && field.table?.scopes.includes(mode));
  const defaults = dashboardPointValues(catalog);
  const templates = mode === "process" ? defaults.resourceTableProcessColumns : defaults.resourceTableColumns;
  const totalIds = new Set(eligible.map(field => field.resourceFactTotalMetricId));
  const identityIds = new Set(["name", "status", "pid", "user", "architecture"]);
  const builtIns: MetricDefinition[] = mode === "performance" ? [] : templates.filter(column => identityIds.has(column.id)
    || totalIds.has(resourceTableMetricId(column))).map(column => ({
      id: column.id, label: resourceTableColumnLabel(column.id), group: uiText.resourceTable.modes[mode], unit: "", preferredSlot: "small", selectable: true }));
  return builtIns.concat(eligible.map(field => ({
    id: field.name, label: resourceTableColumnText({ id: field.name }, fields).label, group: field.reading?.resource ?? uiText.resourceTable.modes[mode],
    unit: field.unit, preferredSlot: "small", selectable: true, sourceId: field.sourceId, valueKind: field.valueKind,
    detail: field.sourceId
  })));
}

export function addTableColumn(columns: readonly ResourceTableColumnSettings[], id: string,
  fields: readonly ProcessMetricField[], mode: ResourceTableViewMode, catalog: MetricDefinition[] = []): ResourceTableColumnSettings[] {
  if (columns.some(column => column.id === id) || !tableFieldCatalog(fields, mode, catalog).some(field => field.id === id)) return [...columns];
  const points = ResourceManager.FrontendPoints;
  const defaults = dashboardPointValues(catalog);
  const template = (mode === "process" ? defaults.resourceTableProcessColumns : defaults.resourceTableColumns).find(column => column.id === id);
  const field = fields.find(field => field.name === id);
  return [...columns, { ...template, id, visible: points.TableColumnNewVisible === "on",
    width: template?.width ?? points.DashboardLayoutLimits.defaultColumnWidth,
    source: points.TableColumnSourceSelection === "first-source" ? field?.table?.sources[0]?.fieldName ?? null : null }];
}

export function removeTableColumn(columns: readonly ResourceTableColumnSettings[], id: string): ResourceTableColumnSettings[] {
  return id === "name" ? [...columns] : columns.filter(column => column.id !== id);
}

/** Visible consumers own the original publication demand; removing/hiding releases it. */
export function tableSampleItems(columns: readonly ResourceTableColumnSettings[], catalog: MetricDefinition[], fields: readonly ProcessMetricField[]): string[] {
  const available = new Set(fields.map(field => field.name));
  const visible = columns.filter(column => column.visible && dashboardMetricAvailable(column.id, column.binding, catalog));
  const ids = visible.map(resourceTableMetricId).filter((id): id is string => id !== null)
    .filter(id => !id.startsWith("process_") || available.has(id));
  return [...new Set([...(visible.length > 0 ? ["process.inventory", "process.attribution"] : []), ...ids])];
}

export function fitTableColumnWidth(texts: readonly string[], measure: (text: string) => number): number {
  const points = ResourceManager.FrontendPoints;
  const limits = points.DashboardLayoutLimits;
  return Math.round(Math.min(limits.maximumColumnWidth, Math.max(limits.minimumColumnWidth,
    texts.reduce((width, text) => Math.max(width, measure(text)), 0) + points.TableColumnFitPadding)));
}
