import type { MetricDefinition, MetricSourceCandidate } from "../../types";
import { metricOfferedInPicker } from "../../presentation/metricPresentation.ts";
import { localizedMetricLabel } from "../../presentation/metricLabels.ts";
import { userFacingMetricGroup } from "../../presentation/userFacingText.ts";
import { uiText } from "../../text.ts";
import { deviceFieldScopeKind, fieldCardId } from "./fieldCards.ts";

/*
 * 先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md）。
 *
 * 项目有两种：
 * - 部件地址（如 cpu.temperature）：来源就是后端目录给它列的候选来源（candidateSources），按后端顺序，
 *   哪个是“自动选择”现在用的由后端的 isSelected 说明。前端不另按读取项重组，候选只有后端这一个出处
 *   （docs/metric_reading_source_selection.md「部件目录的候选来源」）。
 * - 不属于任何部件地址的设备字段：来源是后端读取项 id 相同的那些设备字段（同一台设备的同一物理量，
 *   换个驱动或接口读），按后端 sourceRank 排序；没有部件地址，所以没有“自动选择”。
 * 后端核定为不支持的来源不列。候选字段不在字段目录里（设备还没发现）时卡片读不到它，也不列。
 *
 * “自动选择”就是部件地址本身：卡片存部件地址，后端按本机选来源，和现在一样。
 * 选了具体来源，卡片存“字段@设备”，只读那个字段，它读不到就显示不可用。
 */

export interface MetricSourceChoice {
  /** 选这一项时卡片存的指标 id：自动是部件地址，其余是“字段@设备”。 */
  readonly metricId: string;
  readonly automatic: boolean;
  /** 自动：后端现在给这个部件地址选中的来源（还没选中时为 null）；其余：这个来源本身。 */
  readonly source: MetricDefinition | null;
  /** 后端核定的本机首选。 */
  readonly preferred: boolean;
}

/** 一个来源的名字：字段名加设备名。分组键（cpu、gpu.0 等）和列表的组标题一样翻成当前语言的名字，设备名原样。 */
export function metricSourceName(source: MetricDefinition): string {
  return `${localizedMetricLabel(source.id, source.label)} · ${userFacingMetricGroup(source.group)}`;
}

/** 来源选项的名字：自动选择写出后端现在选的来源（还没选中时只写“自动选择”）。卡片选择器和浮窗共用。 */
export function metricSourceChoiceLabel(choice: MetricSourceChoice): string {
  return choice.automatic
    ? uiText.metricPicker.sourceAutomatic(choice.source ? metricSourceName(choice.source) : null)
    : metricSourceName(choice.source!);
}

/**
 * 当前选中项所在项目的全部来源。只有一个来源（或没有来源）时返回空：没得选，不摆选择。
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
  const candidateId = (candidate: MetricSourceCandidate) => fieldCardId(candidate.fieldName, candidate.deviceIdentity);

  // 选中的是部件地址，或者是某个（列进选择器的）部件地址的候选来源：项目是这个部件地址。
  const address = selected.scopeKind !== deviceFieldScopeKind
    ? selected
    : catalog.find((metric) => metric.scopeKind !== deviceFieldScopeKind && metricOfferedInPicker(metric)
      && (metric.candidateSources ?? []).some((candidate) => candidateId(candidate) === selected.id));
  if (address) {
    const candidates = (address.candidateSources ?? [])
      .filter((candidate) => candidate.sourceAvailability !== "unsupported" && byId.has(candidateId(candidate)));
    const chosen = (address.candidateSources ?? []).find((candidate) => candidate.isSelected);
    const choices: MetricSourceChoice[] = [
      { metricId: address.id, automatic: true, source: chosen ? byId.get(candidateId(chosen)) ?? null : null, preferred: false },
      ...candidates.map((candidate) => ({ metricId: candidateId(candidate), automatic: false,
        source: byId.get(candidateId(candidate))!, preferred: candidate.reading?.isPreferredSource ?? false }))
    ];
    return choices.length > 1 ? choices : [];
  }

  const readingId = selected.reading?.id;
  if (!readingId) return [];
  const sources = catalog
    .filter((metric) => metric.scopeKind === deviceFieldScopeKind
      && metric.reading?.id === readingId && metric.reading.sourceAvailability !== "unsupported")
    // 同名次（例如跨设备显示别名）时本机首选在前。
    .sort((left, right) => left.reading!.sourceRank - right.reading!.sourceRank
      || Number(right.reading!.isPreferredSource) - Number(left.reading!.isPreferredSource));
  return sources.length > 1
    ? sources.map((source) => ({ metricId: source.id, automatic: false, source, preferred: source.reading!.isPreferredSource }))
    : [];
}
