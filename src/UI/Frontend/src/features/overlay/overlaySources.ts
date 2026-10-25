import type { MetricDefinition, OverlayMetricSource } from "../../types.ts";
import { deviceFieldScopeKind, fieldCardSlot } from "../monitor/fieldCards.ts";
import { metricSourceChoiceLabel, metricSourceChoices } from "../monitor/metricSources.ts";

/*
 * 浮窗一项先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md）。
 *
 * 项目是浮窗列表里的一项（部件地址，如 cpu.temperature）。来源和卡片选择器同一出处
 * （features/monitor/metricSources.ts：部件地址的后端候选，自动选择写出后端现在选中的那个）。
 * 自动选择时这一项没有来源记录，原生壳按部件地址订阅；选了具体来源，记下这个候选的字段名和设备身份，
 * 原生壳只按 fields= 读这台设备上的这个字段，读不到时这一行不显示数值，不换成部件地址。
 * 浮窗的 target.* 指标没有候选，不摆选择。
 */

export interface OverlaySourceChoice {
  /** 选这一项时记下的来源；null 是自动选择。 */
  readonly source: OverlayMetricSource | null;
  readonly label: string;
  /** 后端核定的本机首选。 */
  readonly preferred: boolean;
}

/**
 * 一项能选的来源；只有一个来源（或没有）时为空数组，不摆选择。
 * catalog 是浮窗目录加字段目录里全部设备字段（fieldCardDefinitions）。
 */
export function overlaySourceChoices(metricId: string, catalog: readonly MetricDefinition[]): readonly OverlaySourceChoice[] {
  return metricSourceChoices(metricId, catalog).map((choice) => {
    const slot = choice.automatic || !choice.source
      ? null
      : fieldCardSlot(choice.metricId, { scopeKind: deviceFieldScopeKind, scopeKey: choice.source.scopeKey ?? "" });
    return {
      source: slot ? { fieldName: slot.fieldName, deviceIdentity: slot.deviceIdentity } : null,
      label: metricSourceChoiceLabel(choice),
      preferred: choice.preferred
    };
  });
}

/** 当前记下的来源是第几项；记下的来源不在选项里（目录变了）时为 -1，不把它显示成自动选择。 */
export function overlaySourceIndex(choices: readonly OverlaySourceChoice[], current: OverlayMetricSource | undefined): number {
  return choices.findIndex((choice) => current
    ? choice.source?.fieldName === current.fieldName && choice.source.deviceIdentity === current.deviceIdentity
    : choice.source === null);
}

/** 改一项的来源（null 是自动选择，删掉这一项的记录）；只留列表里还有的项目，按 id 排序，保存前后比较稳定。 */
export function withOverlaySource(
  sources: Readonly<Record<string, OverlayMetricSource>>,
  metrics: readonly string[],
  metricId: string,
  source: OverlayMetricSource | null
): Record<string, OverlayMetricSource> {
  const next: Record<string, OverlayMetricSource> = { ...sources };
  if (source) next[metricId] = { ...source };
  else delete next[metricId];
  return overlaySourcesFor(next, metrics);
}

/** 只留列表里还有的项目的来源（项目被移除或换掉时它的来源一起去掉），按 id 排序。 */
export function overlaySourcesFor(
  sources: Readonly<Record<string, OverlayMetricSource>>,
  metrics: readonly string[]
): Record<string, OverlayMetricSource> {
  const kept = new Set(metrics);
  return Object.fromEntries(Object.entries(sources)
    .filter(([id]) => kept.has(id))
    .sort(([left], [right]) => (left < right ? -1 : left > right ? 1 : 0)));
}
