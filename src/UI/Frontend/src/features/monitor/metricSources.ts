import type { MetricDefinition } from "../../types";
import { metricOfferedInPicker } from "../../presentation/metricPresentation.ts";
import { deviceFieldScopeKind, fieldCardId } from "./fieldCards.ts";

/*
 * 先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md）。
 *
 * 一项读数的来源是后端读取项 id 相同的那些设备字段：同一台设备的同一物理量，换个驱动或接口读。
 * 分组只认后端给的读取项 id，排序只认后端的 sourceRank，不按字段名、身份字符串或数值猜。
 * 后端核定为不支持的来源不列。
 *
 * “自动选择”就是部件地址本身（如 cpu.temperature）：卡片存部件地址，后端按本机选首选来源，
 * 和现在一样。选了具体来源，卡片存“字段@设备”，只读那个字段，它读不到就显示不可用。
 */

export interface MetricSourceChoice {
  /** 选这一项时卡片存的指标 id：自动是部件地址，其余是“字段@设备”。 */
  readonly metricId: string;
  readonly automatic: boolean;
  /** 自动：后端现在给这个部件地址选中的来源（还没核定时为 null）；其余：这个来源本身。 */
  readonly source: MetricDefinition | null;
  /** 后端核定的本机首选。 */
  readonly preferred: boolean;
}

/**
 * 当前选中项所在读数的全部来源。只有一个来源（或这一项没有读取项）时返回空：没得选，不摆选择。
 * catalog 是卡片目录（部件地址加全部设备字段，不按读数来源设置筛过）。
 */
export function metricSourceChoices(
  selectedId: string | null,
  catalog: readonly MetricDefinition[]
): readonly MetricSourceChoice[] {
  if (!selectedId) return [];
  const byId = new Map(catalog.map((metric) => [metric.id, metric]));
  const selected = byId.get(selectedId);
  if (!selected) return [];
  // 部件地址带着后端现在给它映射的字段和设备，设备字段就是它自己。
  const sourceOf = (metric: MetricDefinition) => metric.scopeKind === deviceFieldScopeKind
    ? metric
    : metric.fieldName && metric.deviceIdentity ? byId.get(fieldCardId(metric.fieldName, metric.deviceIdentity)) : undefined;
  const readingId = sourceOf(selected)?.reading?.id;
  if (!readingId) return [];

  const sources = catalog
    .filter((metric) => metric.scopeKind === deviceFieldScopeKind
      && metric.reading?.id === readingId && metric.reading.sourceAvailability !== "unsupported")
    // 同名次（例如跨设备显示别名）时本机首选在前。
    .sort((left, right) => left.reading!.sourceRank - right.reading!.sourceRank
      || Number(right.reading!.isPreferredSource) - Number(left.reading!.isPreferredSource));
  // 同一读数可能有跨设备的显示别名（例如 CPU 下的核显温度）；选中的是部件地址就用它，否则用选择器会列出的那个。
  const automatic = selected.scopeKind !== deviceFieldScopeKind
    ? selected
    : catalog.find((metric) => metric.scopeKind !== deviceFieldScopeKind
      && metricOfferedInPicker(metric) && sourceOf(metric)?.reading?.id === readingId);
  const choices: MetricSourceChoice[] = [
    ...(automatic ? [{ metricId: automatic.id, automatic: true, source: sourceOf(automatic) ?? null, preferred: false }] : []),
    ...sources.map((source) => ({ metricId: source.id, automatic: false, source, preferred: source.reading!.isPreferredSource }))
  ];
  return choices.length > 1 ? choices : [];
}
