import type {
  KernelLeakAction,
  KernelLeakImage,
  KernelLeakItem,
  KernelLeakObservation,
  KernelLeakReport,
  KernelLeakRunFailure,
  KernelLeakServices,
  KernelLeakUnit
} from "../../data/kernelLeaks/kernelLeakReport.ts";
import type { SourceSnapshot } from "../../frontendRuntime/source/SourceSnapshot.ts";
import { renderBackendMessage } from "../../presentation/backendMessage.ts";
import { formatBytes, formatSignedBytes } from "../../presentation/byteUnits.ts";
import { formatElapsedDuration } from "../../presentation/elapsedDuration.ts";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { userFacingDateTime } from "../../presentation/userFacingText.ts";
import { uiText } from "../../text.ts";
import type { ContentStateKind } from "../../ui/patterns/ContentState.tsx";

/*
 * 内核泄漏报告的显示文字：只把解码后的报告按当前语言写出来，不推断、不补全。
 * 观察进度照后端给的数写（已观察多少、需要多少），不由前端倒计时或按阈值猜状态。
 * 映像和服务信息的状态不是 Current 时，对应各处都写该状态本身（无法读取／进程已结束）。
 */

/** 没有疑似项列表可显示时的内容状态：读取中、读取失败、观察已关闭、正在观察、未发现疑似项、本轮观察失败。 */
export interface KernelLeakStateView {
  readonly kind: ContentStateKind;
  readonly title: string;
  readonly detail?: string;
}

/** 报告是 Suspected 时返回 null（显示疑似项列表），其余情况返回要显示的内容状态。 */
export function kernelLeakStateView(snapshot: SourceSnapshot<KernelLeakReport>): KernelLeakStateView | null {
  const copy = uiText.kernelLeakReport;
  const report = snapshot.data;
  if (snapshot.status === "error" || snapshot.status === "unavailable" || snapshot.status === "disposed") {
    return { kind: "unavailable", title: copy.readFailed };
  }
  if (!report) return { kind: "waiting", title: copy.loading };
  switch (report.status) {
    case "Disabled": return { kind: "unavailable", title: copy.disabledTitle, detail: copy.disabledDetail };
    case "Observing": return { kind: "waiting", title: copy.observingTitle, detail: copy.observingDetail };
    case "Empty": return { kind: "empty", title: copy.emptyTitle, detail: copy.emptyDetail };
    case "Suspected": return null;
    case "Unavailable": return { kind: "unavailable", title: copy.unavailableTitle, detail: copy.failure[report.failure] };
  }
}

export function kernelLeakCapturedAt(report: KernelLeakReport): string | null {
  return report.capturedAt === null ? null : userFacingDateTime(report.capturedAt);
}

export interface KernelLeakLabelledValue {
  readonly label: string;
  readonly value: string;
}

/** 本轮观察进度：开始时间、观察时长和样本数（各自写已达到多少、需要多少）。观察关闭时没有进度可写，返回 null。 */
export function kernelLeakObservationProgress(report: KernelLeakReport): readonly KernelLeakLabelledValue[] | null {
  if (report.status === "Disabled") return null;
  const copy = uiText.kernelLeakReport.observation;
  const observation: KernelLeakObservation = report.observation;
  return [
    { label: copy.startedAt, value: observation.startedAt === null ? copy.notStarted : userFacingDateTime(observation.startedAt) },
    {
      label: copy.elapsed,
      value: copy.reachedOfRequired(formatElapsedDuration(observation.elapsedSeconds), formatElapsedDuration(observation.requiredSeconds))
    },
    {
      label: copy.samples,
      value: copy.reachedOfRequired(formatInterfaceNumber(observation.sampleCount), formatInterfaceNumber(observation.requiredSampleCount))
    }
  ];
}

/** 保留的上一轮失败：原因、该轮开始时间（没采到样本时写明）、失败时间。 */
export function kernelLeakPreviousRunFailure(failure: KernelLeakRunFailure): readonly KernelLeakLabelledValue[] {
  const copy = uiText.kernelLeakReport;
  return [
    { label: copy.previousRun.reason, value: copy.failure[failure.failure] },
    {
      label: copy.previousRun.startedAt,
      value: failure.startedAt === null ? copy.observation.notStarted : userFacingDateTime(failure.startedAt)
    },
    { label: copy.previousRun.failedAt, value: userFacingDateTime(failure.failedAt) }
  ];
}

/** 失败后何时开始新一轮：本轮失败或带着上一轮失败时写出后端声明的重新开始方式。 */
export function kernelLeakRestartNote(report: KernelLeakReport): string | null {
  if (report.status === "Disabled") return null;
  if (report.status !== "Unavailable" && report.observation.previousRunFailure === null) return null;
  return uiText.kernelLeakReport.restartPolicy[report.observation.restartPolicy];
}

function metadataStatusText(status: "Unavailable" | "ProcessEnded"): string {
  return status === "ProcessEnded" ? uiText.kernelLeakReport.holder.processEnded : uiText.kernelLeakReport.holder.unavailable;
}

/** 持有者标题：映像信息可用时是程序名，否则是映像信息的状态。 */
export function kernelLeakHolderTitle(image: KernelLeakImage): string {
  return image.status === "Current" ? image.name : metadataStatusText(image.status);
}

export function kernelLeakProductName(image: KernelLeakImage): string {
  if (image.status !== "Current") return metadataStatusText(image.status);
  return image.productName ?? uiText.kernelLeakReport.holder.noProductName;
}

export function kernelLeakImagePath(image: KernelLeakImage): string {
  return image.status === "Current" ? image.path : metadataStatusText(image.status);
}

/** 服务名单（每项一个名字），或没有名单时的一句说明。 */
export function kernelLeakServices(services: KernelLeakServices): { readonly names: readonly string[] } | { readonly text: string } {
  if (services.status !== "Current") return { text: metadataStatusText(services.status) };
  return services.names.length > 0 ? { names: services.names } : { text: uiText.kernelLeakReport.holder.noServices };
}

/** 字节量是内核内存，按内存的进制写。 */
function formatUnitValue(value: number, unit: KernelLeakUnit, signed: boolean, fractionDigits = 0): string {
  if (unit === "bytes") return signed ? formatSignedBytes(value, "memory") : formatBytes(value, "memory");
  return formatInterfaceNumber(value, {
    maximumFractionDigits: fractionDigits,
    ...(signed ? { signDisplay: "exceptZero" as const } : {})
  });
}

/** 增长证据，顺序固定：当前值、观察期间增长、增长速率、观察时长、样本数。数值按该项的单位写。 */
export function kernelLeakEvidence(item: KernelLeakItem): readonly KernelLeakLabelledValue[] {
  const copy = uiText.kernelLeakReport;
  return [
    { label: copy.evidence.currentValue, value: formatUnitValue(item.currentValue, item.unit, false) },
    { label: copy.evidence.growth, value: formatUnitValue(item.growth, item.unit, true) },
    { label: copy.evidence.growthPerSecond, value: copy.ratePerSecond(formatUnitValue(item.growthPerSecond, item.unit, true, 2)) },
    { label: copy.evidence.observationTime, value: formatElapsedDuration(item.observationSeconds) },
    { label: copy.evidence.sampleCount, value: formatInterfaceNumber(item.sampleCount) }
  ];
}

export interface KernelLeakActionView {
  readonly action: KernelLeakAction["action"];
  readonly name: string;
  readonly riskLevel: KernelLeakAction["riskLevel"];
  /** 问号里的说明：风险等级和后端给的风险说明。 */
  readonly riskText: string;
  readonly riskLabel: string;
}

export function kernelLeakActionViews(item: KernelLeakItem): readonly KernelLeakActionView[] {
  const copy = uiText.kernelLeakReport.actions;
  return item.actions.map((action) => {
    const name = copy.name[action.action];
    return {
      action: action.action,
      name,
      riskLevel: action.riskLevel,
      riskText: copy.riskText(copy.riskLevel[action.riskLevel], renderBackendMessage(action.risk)),
      riskLabel: copy.riskLabel(name)
    };
  });
}

/** 操作不可用的原因，同一句只写一次（两个操作原因相同时合成一行）。 */
export function kernelLeakActionReasons(item: KernelLeakItem): readonly string[] {
  return [...new Set(item.actions.map((action) => renderBackendMessage(action.disabledReason)))];
}

/** 同一份报告里一个持有者的同一种资源只出现一次；PID 加启动键区分 PID 复用。 */
export function kernelLeakItemKey(item: KernelLeakItem): string {
  return `${item.resourceKind}:${item.holder.processId}:${item.holder.startKey}`;
}
