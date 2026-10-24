import { uiText } from "../../text.ts";
import type { AppCopy } from "../../i18n/copy/index.ts";

type ControlPresentationCopy = AppCopy["control"]["presentation"];

export function controlCapabilityLabel(
  capability: { id: string; label: string }, text: ControlPresentationCopy = uiText.control.presentation
): string {
  if (capability.id === "cpu.power-limit" && capability.label.includes("PL1")) {
    return text.powerLimitPl1;
  }
  return text.labels[capability.id] ?? capability.label;
}

export function controlDisplayText(value: string, text: ControlPresentationCopy = uiText.control.presentation): string {
  const fan = /^风扇\s*(\d+)$/.exec(value);
  const controller = /^显卡风扇控制器（GPU(\d+)）$/.exec(value);
  return fan ? text.fanName(fan[1]) : controller ? text.gpuFanControllerName(controller[1]) : text.text[value] ?? value;
}

/** Display only stable reasons or a known localized legacy message; raw detail is diagnostic. */
export function controlOutcomeReason(
  outcome: import("./controlTypes.ts").ControlApplyOutcome | undefined,
  copy: AppCopy["control"] = uiText.control
): string | null {
  if (!outcome || outcome.status === "applied") return null;
  const text = copy.recovery;
  const reason = outcome.reason;
  switch (reason?.code) {
    case "control-object-missing": return text.objectMissing;
    case "control-capability-missing": return text.capabilityMissing;
    case "control-writer-missing": return text.writerMissing;
    case "control-backend-unavailable": return text.backendUnavailable;
    case "control-release-failed": return text.releaseFailed;
    case "control-unit-mismatch": return text.unitMismatch;
    case "control-write-failed": return text.writeFailed;
    case "control-write-rejected": return text.writeRejected;
    case "startup-profile-read-only": return text.readOnly;
    case "control-access-level-required": {
      const level = reason.arguments?.requiredAccessLevel;
      return level ? text.accessRequired(copy.accessLevel.name[level]) : text.writeRejected;
    }
    case "control-out-of-range": {
      const args = reason.arguments;
      return args?.minimum != null && args.maximum != null && args.unit != null
        ? text.outOfRange(args.minimum, args.maximum, controlDisplayText(args.unit, copy.presentation)) : text.writeRejected;
    }
    default: return outcome.message && copy.presentation.text[outcome.message] || text.writeRejected;
  }
}
