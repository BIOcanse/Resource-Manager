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
import { formatElapsedDuration } from "../../presentation/elapsedDuration.ts";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { userFacingDateTime } from "../../presentation/userFacingText.ts";
import { compactUserDetailSections, userDetailItem, userDetailSection, type UserDetailSection } from "../../presentation/userDetails.ts";
import { currentLanguage, uiText } from "../../text.ts";
import type { ContentStateKind } from "../../ui/patterns/ContentState.tsx";

/* Display backend progress and metadata availability without inferring state or completing missing data. */

/** Observation status is independent of the entry list. */
export interface KernelLeakStateView {
  readonly kind: ContentStateKind;
  readonly title: string;
  readonly detail?: string;
}

/** Return null for Suspected because active entries already show the result. */
export function kernelLeakStateView(snapshot: SourceSnapshot<KernelLeakReport>): KernelLeakStateView | null {
  const copy = uiText.kernelLeakReport;
  const report = snapshot.data;
  if (snapshot.status === "error" || snapshot.status === "unavailable" || snapshot.status === "disposed") {
    return { kind: "unavailable", title: copy.readFailed };
  }
  if (!report) return { kind: "waiting", title: copy.loading };
  switch (report.status) {
    case "NotObserved": return { kind: "unavailable", title: copy.notObservedTitle, detail: copy.notObservedDetail };
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

/** Return backend progress, or null when no observation is running. */
export function kernelLeakObservationProgress(report: KernelLeakReport): readonly KernelLeakLabelledValue[] | null {
  if (report.status === "NotObserved") return null;
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

/** Show the retained failure reason and timestamps. */
export function kernelLeakPreviousRunFailure(failure: KernelLeakRunFailure): readonly KernelLeakLabelledValue[] {
  const copy = uiText.kernelLeakReport;
  return [
    { label: copy.previousRun.reason, value: copy.failure[failure.failure] },
    { label: copy.previousRun.startedAt, value: userFacingDateTime(failure.startedAt) },
    { label: copy.previousRun.failedAt, value: userFacingDateTime(failure.failedAt) }
  ];
}

/** Show the backend-declared restart behavior after a failure. */
export function kernelLeakRestartNote(report: KernelLeakReport): string | null {
  if (report.status === "NotObserved") return null;
  if (report.status !== "Unavailable" && report.observation.previousRunFailure === null) return null;
  return uiText.kernelLeakReport.restartPolicy[report.observation.restartPolicy];
}

function metadataStatusText(status: "Unavailable" | "ProcessEnded"): string {
  return status === "ProcessEnded" ? uiText.kernelLeakReport.holder.processEnded : uiText.kernelLeakReport.holder.unavailable;
}

/** Use the image name when available; otherwise show its availability state. */
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

/** Show service names or explain their absence. */
export function kernelLeakServices(services: KernelLeakServices): { readonly names: readonly string[] } | { readonly text: string } {
  if (services.status !== "Current") return { text: metadataStatusText(services.status) };
  return services.names.length > 0 ? { names: services.names } : { text: uiText.kernelLeakReport.holder.noServices };
}

/** Show holder identity, detection evidence, action risks, and unavailability reasons. */
export function kernelLeakItemDetails(item: KernelLeakItem): { readonly title: string; readonly sections: UserDetailSection[] } {
  const copy = uiText.kernelLeakReport;
  const holder = item.holder;
  const services = kernelLeakServices(holder.services);
  const serviceText = "names" in services
    ? new Intl.ListFormat(currentLanguage(), { type: "conjunction" }).format(services.names)
    : services.text;
  const actions = kernelLeakActionViews(item);
  return {
    title: copy.details.title(kernelLeakHolderTitle(holder.image)),
    sections: compactUserDetailSections([
      userDetailSection(copy.details.holderSection, [
        userDetailItem(copy.details.processName, kernelLeakHolderTitle(holder.image)),
        userDetailItem(copy.holder.processId, formatInterfaceNumber(holder.processId, { useGrouping: false })),
        userDetailItem(copy.details.startKey, holder.startKey),
        userDetailItem(copy.holder.productName, kernelLeakProductName(holder.image)),
        userDetailItem(copy.holder.services, serviceText),
        userDetailItem(copy.holder.imagePath, kernelLeakImagePath(holder.image))
      ]),
      userDetailSection(copy.details.evidenceSection, [
        userDetailItem(copy.details.state, item.state === "history" ? copy.historyEntry : copy.details.active),
        userDetailItem(copy.details.resourceKind, copy.resourceKind[item.resourceKind]),
        ...kernelLeakEvidence(item).map((entry) => userDetailItem(entry.label, entry.value))
      ]),
      userDetailSection(copy.actions.title, [
        ...actions.map((action) => userDetailItem(action.name, action.riskText)),
        userDetailItem(copy.details.unavailableReason, kernelLeakActionReasons(item).join(" "))
      ])
    ])
  };
}

/** Keep unit formatting exhaustive for the supported contract. */
const unitFormats: Readonly<Record<KernelLeakUnit, (value: number, signed: boolean, fractionDigits: number) => string>> = {
  count: (value, signed, fractionDigits) => formatInterfaceNumber(value, {
    maximumFractionDigits: fractionDigits,
    ...(signed ? { signDisplay: "exceptZero" as const } : {})
  })
};

function formatUnitValue(value: number, unit: KernelLeakUnit, signed: boolean, fractionDigits = 0): string {
  return unitFormats[unit](value, signed, fractionDigits);
}

/** Keep evidence ordering stable and use the item unit. */
export function kernelLeakEvidence(item: KernelLeakItem): readonly KernelLeakLabelledValue[] {
  const copy = uiText.kernelLeakReport;
  return [
    { label: copy.evidence.currentValue, value: formatUnitValue(item.currentValue, item.unit, false) },
    { label: copy.evidence.growth, value: formatUnitValue(item.growth, item.unit, true) },
    { label: copy.evidence.growthPerSecond, value: copy.ratePerSecond(formatUnitValue(item.growthPerSecond, item.unit, true, 2)) },
    { label: copy.evidence.observationTime, value: formatElapsedDuration(item.observationSeconds) },
    { label: copy.evidence.sampleCount, value: formatInterfaceNumber(item.sampleCount) },
    { label: copy.evidence.firstObserved, value: userFacingDateTime(item.firstObservedAt) },
    { label: copy.evidence.lastObserved, value: userFacingDateTime(item.lastObservedAt) }
  ];
}

export interface KernelLeakActionView {
  readonly action: KernelLeakAction["action"];
  readonly name: string;
  readonly riskLevel: KernelLeakAction["riskLevel"];
  /** Show the backend risk level and explanation. */
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

/** Deduplicate identical action-unavailability reasons. */
export function kernelLeakActionReasons(item: KernelLeakItem): readonly string[] {
  return [...new Set(item.actions.map((action) => renderBackendMessage(action.disabledReason)))];
}

/** Place active entries before history, preserving backend order within each group. */
export function kernelLeakItemsInDisplayOrder(items: readonly KernelLeakItem[]): readonly KernelLeakItem[] {
  return [...items.filter((item) => item.state === "active"), ...items.filter((item) => item.state === "history")];
}

/** Show active, historical, and capacity-dropped counts. */
export function kernelLeakItemCounts(report: KernelLeakReport): readonly KernelLeakLabelledValue[] {
  const copy = uiText.kernelLeakReport;
  const active = report.items.filter((item) => item.state === "active").length;
  return [
    { label: copy.suspectedItems, value: formatInterfaceNumber(active) },
    { label: copy.historyItems, value: formatInterfaceNumber(report.items.length - active) },
    ...(report.unrecordedItemCount > 0 ? [{ label: copy.unrecordedItems, value: formatInterfaceNumber(report.unrecordedItemCount) }] : [])
  ];
}
