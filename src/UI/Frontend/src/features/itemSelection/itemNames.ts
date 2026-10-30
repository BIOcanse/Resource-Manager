import { localizedMetricLabel } from "../../presentation/metricLabels.ts";
import { uiText } from "../../text.ts";
import { automaticSource, type ItemKind } from "./itemSelectionModel.ts";

/** 后端给目标软件资源细分指标的来源名（TargetedRecordingItemCatalogRules.ResourceBreakdownSource）。 */
export const resourceBreakdownSource = "resource-breakdown";

/**
 * 录制项的名称，选择器、报告的录制项表和结果视图的序列都用它：
 * - 应用提交帧用自己的名称；
 * - 整机和硬件（metric）用监视页的指标名；
 * - 目标软件（software）先查字段名称（进程字段），再查指标名（资源细分的 cpu.usage 等）；
 * 都没有时显示 id 本身。
 */
export function itemName(kind: ItemKind, id: string): string {
  if (kind === "frames") return uiText.targetedReport.itemPicker.framesItem;
  if (kind === "software") {
    const field = (uiText.fieldLabel as Record<string, { label: string } | undefined>)[id];
    if (field) return field.label;
  }
  return localizedMetricLabel(id);
}

/**
 * 来源的名称：自动选择、资源细分用文案；整机指标的具体来源是“字段@设备”，写成字段名加设备标识
 * （和监视页的来源名一样是“名称 · 设备”）；其他来源原样显示。
 */
export function sourceName(source: string): string {
  const copy = uiText.targetedReport.itemPicker;
  if (source === automaticSource) return copy.sourceAuto;
  if (source === resourceBreakdownSource) return copy.sourceResourceBreakdown;
  const separator = source.lastIndexOf("@");
  if (separator > 0 && separator < source.length - 1) {
    const fieldName = source.slice(0, separator);
    const label = (uiText.fieldLabel as Record<string, { label: string } | undefined>)[fieldName]?.label ?? fieldName;
    return `${label} · ${source.slice(separator + 1)}`;
  }
  return source;
}
