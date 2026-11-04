import type { ControlItemStatus } from "../features/control/controlStateMachine.ts";

/** Labels publish severity through data-tone; status-tone.css owns colors. Accent denotes selection or actions, not severity. */
export type StatusTone = "normal" | "info" | "warning" | "error" | "neutral";

/** Backend info/warning/critical map to severity; unknown values stay neutral. */
export function softwareIssueTone(severity: string): StatusTone {
  switch (severity) {
    case "critical": return "error";
    case "warning": return "warning";
    case "info": return "info";
    default: return "neutral";
  }
}

/** Unset controls have no label but return neutral for type completeness. */
export function controlItemTone(status: ControlItemStatus): StatusTone {
  switch (status) {
    case "applied": return "normal";
    case "edited":
    case "applying": return "info";
    case "failed":
    case "unsupported": return "error";
    case "unset": return "neutral";
  }
}

/** Missing installers need attention; available updates are informational. */
export function componentStateTone(state: string | undefined): StatusTone {
  switch (state) {
    case "payload_missing": return "warning";
    case "update_available": return "info";
    default: return "neutral";
  }
}

/** Task success is healthy; failed and uncertain outcomes are errors; queued, running, and canceled remain neutral. */
export function taskStatusTone(status: string, stateUncertain: boolean): StatusTone {
  if (status === "failed" || stateUncertain) return "error";
  return status === "succeeded" ? "normal" : "neutral";
}
