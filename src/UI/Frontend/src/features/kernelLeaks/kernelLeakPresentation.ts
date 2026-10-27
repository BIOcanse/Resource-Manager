import type { KernelLeakImage, KernelLeakItem, KernelLeakReport, KernelLeakServices } from "../../data/kernelLeaks/kernelLeakReport.ts";
import type { SourceSnapshot } from "../../frontendRuntime/source/SourceSnapshot.ts";
import { formatElapsedDuration } from "../../presentation/elapsedDuration.ts";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { userFacingDateTime } from "../../presentation/userFacingText.ts";
import { uiText } from "../../text.ts";
import type { ContentStateKind } from "../../ui/patterns/ContentState.tsx";

/*
 * 内核泄漏报告的显示文字：只把解码后的报告按当前语言写出来，不推断、不补全。
 * 映像和服务信息的状态不是 Current 时，对应各处都写该状态本身（无法读取／进程已结束）。
 */

/** 没有疑似项列表可显示时的内容状态：读取中、读取失败、未发现疑似项、尚未取得首次采样、报告不可用。 */
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
    case "Empty": return { kind: "empty", title: copy.emptyTitle, detail: copy.emptyDetail };
    case "Suspected": return null;
    case "Unavailable": return report.failure === "NotObserved"
      ? { kind: "waiting", title: copy.notObservedTitle, detail: copy.failure.NotObserved }
      : { kind: "unavailable", title: copy.unavailableTitle, detail: copy.failure[report.failure] };
  }
}

export function kernelLeakCapturedAt(report: KernelLeakReport): string | null {
  return report.capturedAt === null ? null : userFacingDateTime(report.capturedAt);
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
  return image.status === "Current" ? image.imagePath : metadataStatusText(image.status);
}

/** 服务名单（每项一个名字），或没有名单时的一句说明。 */
export function kernelLeakServices(services: KernelLeakServices): { readonly names: readonly string[] } | { readonly text: string } {
  if (services.status !== "Current") return { text: metadataStatusText(services.status) };
  return services.names.length > 0 ? { names: services.names } : { text: uiText.kernelLeakReport.holder.noServices };
}

export interface KernelLeakEvidenceView {
  readonly label: string;
  readonly value: string;
}

/** 增长证据，顺序固定：当前数量、观察期间增长、增长速率、观察时长、样本数。 */
export function kernelLeakEvidence(item: KernelLeakItem): readonly KernelLeakEvidenceView[] {
  const copy = uiText.kernelLeakReport;
  return [
    { label: copy.evidence.currentValue, value: formatInterfaceNumber(item.currentValue) },
    { label: copy.evidence.growth, value: formatInterfaceNumber(item.growth, { signDisplay: "exceptZero" }) },
    {
      label: copy.evidence.growthPerSecond,
      value: copy.ratePerSecond(formatInterfaceNumber(item.growthPerSecond, { maximumFractionDigits: 2, signDisplay: "exceptZero" }))
    },
    { label: copy.evidence.observationTime, value: formatElapsedDuration(item.observationSeconds) },
    { label: copy.evidence.sampleCount, value: formatInterfaceNumber(item.sampleCount) }
  ];
}

/** 同一份报告里一个持有者的同一种资源只出现一次；PID 加启动键区分 PID 复用。 */
export function kernelLeakItemKey(item: KernelLeakItem): string {
  return `${item.resourceKind}:${item.holder.processId}:${item.holder.startKey}`;
}
