import type {
  HostManagerCycleFailure,
  HostManagerFactExclusion,
  HostManagerSchedulingExclusionReason,
  HostManagerSchedulingFact,
  HostManagerSmartCoordinatorStatus
} from "../../types.ts";
import { compactUserDetailSections, userDetailItem, userDetailSection } from "../../presentation/userDetails.ts";
import { uiText } from "../../text.ts";

/**
 * 调度段失败的文字：失败类型、原生计划状态、事实和排除原因都是解码器保证的封闭集合，每个码都有文字，直接取。
 * 原生计划的排除原因就是计划状态码，文字表里用同一份文字。数量和标识原样显示，前端不计算。
 */
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

/** 排除明细对话框：每项一节，标题是事实和原因，下面是后端给出的进程与对象标识，有 GPU 适配器标识时另列一行。 */
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
