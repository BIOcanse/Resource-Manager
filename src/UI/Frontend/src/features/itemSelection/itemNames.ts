import { localizedMetricLabel } from "../../presentation/metricLabels.ts";
import { uiText } from "../../text.ts";
import { automaticSource, type ItemKind } from "./itemSelectionModel.ts";

/** Backend source identifier for targeted software resource-breakdown metrics. */
export const resourceBreakdownSource = "resource-breakdown";

/** Shared item naming: frame name, monitoring metric name, or software field then metric name; fall back to identity. */
export function itemName(kind: ItemKind, id: string): string {
  if (kind === "frames") return uiText.targetedReport.itemPicker.framesItem;
  if (kind === "software") {
    const field = (uiText.fieldLabel as Record<string, { label: string } | undefined>)[id];
    if (field) return field.label;
  }
  return localizedMetricLabel(id);
}

/** Recording gaps, overlays, and the picker share localized unavailable reasons; preserve unknown reason codes. */
export function itemReasonText(reason: string): string {
  return (uiText.performanceOverlay.itemReasons as Record<string, string | undefined>)[reason] ?? reason;
}

/** Localize automatic and breakdown sources; format metric sources as field and device, preserving other source names. */
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
