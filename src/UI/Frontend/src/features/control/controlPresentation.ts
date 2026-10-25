import { uiText } from "../../text.ts";
import type { AppCopy } from "../../i18n/copy/index.ts";
import type { ControlApplyOutcome, ControlObject, ControlInstance } from "./controlTypes.ts";
import { controlReasonText } from "../../presentation/controlReasonText.ts";

type ControlPresentationCopy = AppCopy["control"]["presentation"];

export function controlCapabilityLabel(
  capability: { id: string; channel?: string | null }, text: ControlPresentationCopy = uiText.control.presentation
): string {
  if (capability.id === "cpu.power-limit" && capability.channel === "intel-msr") {
    return text.powerLimitPl1;
  }
  return text.labels[capability.id] ?? capability.id;
}

export function controlObjectName(
  object: Pick<ControlObject | ControlInstance, "kind" | "reportedName" | "ordinal" | "adapterIndex">,
  text: AppCopy["control"] = uiText.control
): string {
  if (object.reportedName) return object.reportedName;
  if (object.kind === "fan") return object.ordinal != null
    ? text.presentation.fanName(String(object.ordinal + 1))
    : object.adapterIndex != null ? text.presentation.gpuFanControllerName(String(object.adapterIndex)) : text.kind.fan;
  return object.kind === "cpu" ? text.kind.cpu : text.kind.gpu;
}

/** Units are protocol identities; only the frontend chooses their displayed spelling. */
export function controlDisplayText(unit: string, text: ControlPresentationCopy = uiText.control.presentation): string {
  return unit === "档" ? text.text["档"] : unit;
}

export function controlOutcomeReason(
  outcome: ControlApplyOutcome | undefined,
  copy: AppCopy["control"] = uiText.control
): string | null {
  return !outcome || outcome.status === "applied" ? null : controlReasonText(outcome.reason, copy);
}
