import type {
  HostManagerCycleFailure,
  HostManagerFactExclusion,
  HostManagerSmartCoordinatorStatus
} from "../../types.ts";
import { compactUserDetailSections, userDetailItem, userDetailSection } from "../../presentation/userDetails.ts";
import { uiText } from "../../text.ts";

/**
 * 调度段失败的文字：失败类型和原生计划状态是解码器保证的封闭集合，直接取文字。
 * 事实名和排除原因是后端的开放字符串：有文字的码显示文字，没有文字的码按后端发来的原样显示；
 * 原生计划的排除原因就是计划状态码，用同一份文字。数量和标识原样显示，前端不计算。
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

export function smartFailureFactText(fact: string): string {
  return uiText.smartFailure.facts[fact] ?? fact;
}

export function smartFailureReasonText(reason: string): string {
  const nativePlanStatuses: Readonly<Record<string, string | undefined>> = uiText.smartFailure.nativePlanStatuses;
  return uiText.smartFailure.reasons[reason] ?? nativePlanStatuses[reason] ?? reason;
}

export function smartFailureExclusionLabel(fact: string, reason: string): string {
  return `${smartFailureFactText(fact)} · ${smartFailureReasonText(reason)}`;
}

/** 排除明细对话框：每项一节，标题是事实和原因，下面是后端给出的进程与对象标识。 */
export function smartFailureExclusionSections(status: HostManagerSmartCoordinatorStatus) {
  return compactUserDetailSections(status.factExclusions.map(exclusionSection));
}

function exclusionSection(exclusion: HostManagerFactExclusion) {
  const field = uiText.smartFailure.field;
  const identified = exclusion.processId !== null
    || exclusion.processStartKey !== null
    || exclusion.targetId !== null;
  return userDetailSection(smartFailureExclusionLabel(exclusion.fact, exclusion.reason), identified
    ? [
      userDetailItem(field.processId, exclusion.processId),
      userDetailItem(field.processStartKey, exclusion.processStartKey),
      userDetailItem(field.targetId, exclusion.targetId)
    ]
    : [userDetailItem(field.target, uiText.smartFailure.noTarget)]);
}
