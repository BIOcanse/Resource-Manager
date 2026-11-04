import { controlOutcomeReason } from "./controlPresentation.ts";
import type { ControlSetting, ControlApplyOutcome, ControlStateView } from "./controlTypes.ts";

/** Derive capability status from persisted desired state, local draft, and execution receipts without duplicate status flags. */
export type ControlItemStatus =
  /** No setting exists. */
  | "unset"
  /** Local edits have not been submitted. */
  | "edited"
  /** Submitted and awaiting a receipt. */
  | "applying"
  /** Execution succeeded. */
  | "applied"
  /** Unavailable on this platform. */
  | "unsupported"
  /** Execution was attempted and failed. */
  | "failed";

export interface ControlItemView {
  capabilityId: string;
  status: ControlItemStatus;
  /** Reason for any state other than applied; otherwise null. */
  message: string | null;
}

function sameValue(left: ControlSetting, right: ControlSetting): boolean {
  if (left.gpuVfCurvePresetId != null || right.gpuVfCurvePresetId != null) {
    return (left.gpuVfCurvePresetId ?? null) === (right.gpuVfCurvePresetId ?? null);
  }
  if ((left.gpuVfCurvePresetId ?? null) !== (right.gpuVfCurvePresetId ?? null)
    || left.number !== right.number || left.toggle !== right.toggle
    || left.unit !== right.unit || left.curveExecution !== right.curveExecution) {
    return false;
  }
  const leftCurve = left.curve ?? null;
  const rightCurve = right.curve ?? null;
  if (leftCurve === null || rightCurve === null) {
    return leftCurve === rightCurve;
  }
  return leftCurve.length === rightCurve.length
    && leftCurve.every((point, index) =>
      point.temperatureCelsius === rightCurve[index].temperatureCelsius
      && point.percent === rightCurve[index].percent);
}

function find(
  settings: readonly ControlSetting[],
  capabilityId: string
): ControlSetting | undefined {
  return settings.find((setting) => setting.capabilityId === capabilityId);
}

/** Local edits take precedence over receipts describing older values. */
export function controlItemStatusOf(
  capabilityId: string,
  edited: readonly ControlSetting[],
  saved: readonly ControlSetting[],
  outcomes: readonly ControlApplyOutcome[],
  submitting: boolean
): ControlItemView {
  const editedSetting = find(edited, capabilityId);
  const savedSetting = find(saved, capabilityId);

  if ((!editedSetting && savedSetting) || (editedSetting && (!savedSetting || !sameValue(editedSetting, savedSetting)))) {
    return {
      capabilityId,
      status: submitting ? "applying" : "edited",
      message: null
    };
  }
  if (submitting) {
    return { capabilityId, status: "applying", message: null };
  }
  if (!savedSetting) {
    const failedRelease = outcomes.find((entry) => entry.capabilityId === capabilityId && entry.status === "failed");
    if (failedRelease) return { capabilityId, status: "failed", message: controlOutcomeReason(failedRelease) };
    return { capabilityId, status: "unset", message: null };
  }

  const outcome = outcomes.find((entry) => entry.capabilityId === capabilityId);
  if (!outcome) {
    // A persisted item missing from the receipt has not yet been applied.
    return { capabilityId, status: "applying", message: null };
  }
  if (outcome.status === "applied") {
    return { capabilityId, status: "applied", message: null };
  }
  return {
    capabilityId,
    status: outcome.status === "unsupported" ? "unsupported" : "failed",
    message: controlOutcomeReason(outcome)
  };
}

/** Submit the complete object settings so removal is expressible; return nothing when unchanged. */
export function hasPendingChanges(
  edited: readonly ControlSetting[],
  saved: readonly ControlSetting[]
): boolean {
  if (edited.length !== saved.length) {
    return true;
  }
  return edited.some((setting) => {
    const previous = find(saved, setting.capabilityId);
    return !previous || !sameValue(setting, previous);
  });
}

/** Retry is derived from authoritative state, independent of the draft. */
export function canRetryControlObject(state: ControlStateView | null, objectId: string): boolean {
  return !!state && ((state.desired.pendingReleases ?? []).some(target => target.objectId === objectId && target.settings.length > 0)
    || state.lastApply.outcomes.some(outcome => outcome.objectId === objectId && outcome.status !== "applied"
      && state.desired.objects.some(target => target.objectId === objectId
        && target.settings.some(setting => setting.capabilityId === outcome.capabilityId))));
}
