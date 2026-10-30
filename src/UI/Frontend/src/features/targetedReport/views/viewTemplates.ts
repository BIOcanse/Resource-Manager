import {
  frontendPreferencesClient, type FrontendPreferencesClient
} from "../../../preferences/frontendPreferences.ts";
import { seriesIdForRole, type SeriesSet } from "./seriesModel.ts";
import {
  createView, parseViewFields, viewDomain, xSeriesId,
  type ReportView, type ViewAxisSide, type ViewX
} from "./viewModel.ts";

/**
 * 视图模板（docs/targeted_recording_views_2026-10-30.md §3“视图”）：存在前端偏好的
 * targetedReportViewTemplates 段（浏览器存储或原生壳的 FrontendPreferencesOwner，保存范围
 * "targetedReportViewTemplates"），可以套用到任意一份报告。
 *
 * 模板里的序列按 role 存（frames、frameRate、software:<指标>、system:<指标>），不存这份报告的序列 id；
 * 套用时按 role 和视图的横轴找回目标报告里的 id。目标报告没有这个 role 时原样留下 role，
 * 视图上会写明“这份报告没有这条序列”，不悄悄删掉。
 */
export interface TemplateView extends Omit<ReportView, "id" | "x" | "y"> {
  x: ViewX;
  y: Array<{ role: string; axis: ViewAxisSide }>;
}

export interface TargetedReportViewTemplate {
  id: string;
  name: string;
  views: TemplateView[];
}

/** 与原生壳 FrontendReportViewTemplatesCache 的上限相同。 */
export const templateLimits = { templates: 200, viewsPerTemplate: 64, nameLength: 80 } as const;

/** 视图可能是 Solid store 的代理，structuredClone 复制不了代理，所以按 JSON 复制（字段都是 JSON 值）。 */
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

/** 读回存储的模板：形状不对就抛错（界面显示“视图模板读写失败”），不丢弃、不修补。 */
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
