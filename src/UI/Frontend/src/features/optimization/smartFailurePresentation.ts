import type {
  HostManagerCycleFailure,
  HostManagerFactExclusion,
  HostManagerSchedulingExclusionReason,
  HostManagerSchedulingFact,
  HostManagerSmartCoordinatorStatus
} from "../../types.ts";
import { compactUserDetailSections, userDetailItem, userDetailSection } from "../../presentation/userDetails.ts";
import { uiText } from "../../text.ts";

/** Decoded failure codes are exhaustive; localize them directly and preserve backend counts and identities. */
export function smartFailureTitle(failure: HostManagerCycleFailure): string {
  return uiText.smartFailure.title[failure.kind];
}

export function smartFailureHint(failure: HostManagerCycleFailure): string {
  return uiText.smartFailure.hint[failure.kind];
}

export function smartFailureNativePlanStatus(failure: HostManagerCycleFailure): string | null {
  return failure.nativePlanStatus === null
    ? null
    : uiText.smartFailure.nativePlanStatus(uiText.smartFailure.nativePlanStatuses[failure.nativePlanStatus]);
}

export function smartFailureFactText(fact: HostManagerSchedulingFact): string {
  return uiText.smartFailure.facts[fact];
}

export function smartFailureReasonText(reason: HostManagerSchedulingExclusionReason): string {
  return uiText.smartFailure.reasons[reason];
}

export function smartFailureExclusionLabel(fact: HostManagerSchedulingFact, reason: HostManagerSchedulingExclusionReason): string {
  return `${smartFailureFactText(fact)} · ${smartFailureReasonText(reason)}`;
}

/** Show each exclusion with its facts, reason, process and object IDs, and optional GPU identity. */
export function smartFailureExclusionSections(status: HostManagerSmartCoordinatorStatus) {
  return compactUserDetailSections(status.factExclusions.map(exclusionSection));
}

function exclusionSection(exclusion: HostManagerFactExclusion) {
  const field = uiText.smartFailure.field;
  const identified = exclusion.processId !== null
    || exclusion.processStartKey !== null
    || exclusion.targetId !== null;
  const identity = identified
    ? [
      userDetailItem(field.processId, exclusion.processId),
      userDetailItem(field.processStartKey, exclusion.processStartKey),
      userDetailItem(field.targetId, exclusion.targetId)
    ]
    : [userDetailItem(field.target, uiText.smartFailure.noTarget)];
  return userDetailSection(smartFailureExclusionLabel(exclusion.fact, exclusion.reason), [
    ...identity,
    userDetailItem(field.adapterKey, exclusion.adapterKey)
  ]);
}
