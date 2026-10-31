import { uiText } from "../../text.ts";
import type { AppCopy } from "../../i18n/copy/index.ts";
import type { ControlApplyOutcome, ControlObject, ControlInstance } from "./controlTypes.ts";
import { controlReasonText } from "../../presentation/controlReasonText.ts";

type ControlPresentationCopy = AppCopy["control"]["presentation"];

export function controlCapabilityLabel(
  capability: { id: string; channel?: string | null }, text: ControlPresentationCopy = uiText.control.presentation
): string {
  if (capability.id === "gpu.core-vf-curve") return uiText.gpuVfCurve.title;
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

/**
 * 按编号的显卡风扇控制器（没有自报名字、带 adapterIndex 的风扇）属于哪块显卡：同一份对象目录里同编号的显卡对象自报的名字。
 * 编号是后端发的原样（和监视页 gpu.N 同一套），名字只取后端发的 reportedName；没有就为 null，界面只写编号，不猜。
 */
export function controlFanGpuName(
  object: Pick<ControlObject | ControlInstance, "kind" | "reportedName" | "adapterIndex">,
  objects: readonly Pick<ControlObject | ControlInstance, "kind" | "reportedName" | "adapterIndex">[]
): string | null {
  if (object.kind !== "fan" || object.reportedName || object.adapterIndex == null) return null;
  const names = new Set(objects
    .filter((other) => other.kind === "gpu" && other.adapterIndex === object.adapterIndex)
    .map((other) => other.reportedName?.trim())
    .filter((name): name is string => Boolean(name)));
  // 同一编号对上两个不同名字时不写，免得写错。
  return names.size === 1 ? [...names][0] : null;
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
