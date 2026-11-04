import {
  frontendPreferencesClient, type FrontendPreferencesClient
} from "../../../preferences/frontendPreferences.ts";
import { seriesIdForRole, type SeriesSet } from "./seriesModel.ts";
import {
  createView, parseViewFields, viewDomain, xSeriesId,
  type ReportView, type ViewAxisSide, type ViewX
} from "./viewModel.ts";

/** Persist templates in targetedReportViewTemplates using stable roles. Resolve roles for the target report and retain unresolved references for visible validation. */
export interface TemplateView extends Omit<ReportView, "id" | "x" | "y"> {
  x: ViewX;
  y: Array<{ role: string; axis: ViewAxisSide }>;
}

export interface TargetedReportViewTemplate {
  id: string;
  name: string;
  views: TemplateView[];
}

/** Match the native template-cache limit. */
export const templateLimits = { templates: 200, viewsPerTemplate: 64, nameLength: 80 } as const;

/** Clone JSON fields explicitly because structuredClone cannot copy Solid store proxies. */
const plainCopy = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;

const roleOf = (set: SeriesSet, seriesId: string) => set.byId.get(seriesId)?.role ?? seriesId;

export function templateFromViews(id: string, name: string, views: readonly ReportView[], set: SeriesSet): TargetedReportViewTemplate {
  return {
    id, name,
    views: views.map((view) => {
      const { id: _id, x, y, ...fields } = plainCopy(view);
      const xId = xSeriesId(x);
      return { ...fields, x: xId === null ? x : `series:${roleOf(set, xId)}`,
        y: y.map((item) => ({ role: roleOf(set, item.seriesId), axis: item.axis })) };
    })
  };
}

export function viewsFromTemplate(template: TargetedReportViewTemplate, set: SeriesSet, nextId: () => string): ReportView[] {
  return template.views.map((view) => {
    const { x, y, name, ...fields } = view;
    const domain = viewDomain(x);
    const xRole = xSeriesId(x);
    return createView(nextId(), name, {
      ...plainCopy(fields),
      x: xRole === null ? x : `series:${seriesIdForRole(set, xRole, "time") ?? xRole}`,
      y: y.map((item) => ({ seriesId: seriesIdForRole(set, item.role, domain) ?? item.role, axis: item.axis }))
    });
  });
}

/** Reject malformed stored templates visibly instead of discarding or repairing them. */
export function decodeTemplates(value: unknown): TargetedReportViewTemplate[] {
  if (value === null || value === undefined) return [];
  if (!Array.isArray(value) || value.length > templateLimits.templates) throw new Error("targeted-report-view-templates");
  const ids = new Set<string>();
  return value.map((item) => {
    const record = item as Record<string, unknown> | null;
    if (typeof record !== "object" || record === null || typeof record.id !== "string" || !record.id || ids.has(record.id)
        || typeof record.name !== "string" || record.name.length > templateLimits.nameLength
        || !Array.isArray(record.views) || record.views.length === 0 || record.views.length > templateLimits.viewsPerTemplate) {
      throw new Error("targeted-report-view-templates");
    }
    ids.add(record.id);
    return { id: record.id, name: record.name, views: record.views.map((view): TemplateView => {
      const fields = parseViewFields(view, "role");
      return { ...fields, y: fields.y.map((entry) => ({ role: entry.ref, axis: entry.axis })) };
    }) };
  });
}

export async function readViewTemplates(client: FrontendPreferencesClient = frontendPreferencesClient()) {
  return decodeTemplates((await client.read()).targetedReportViewTemplates);
}

export async function saveViewTemplates(templates: readonly TargetedReportViewTemplate[],
  client: FrontendPreferencesClient = frontendPreferencesClient()) {
  const value = decodeTemplates(plainCopy(templates));
  const current = await client.read();
  const saved = await client.save({ ...current, targetedReportViewTemplates: value }, "targetedReportViewTemplates");
  return decodeTemplates(saved.targetedReportViewTemplates);
}
