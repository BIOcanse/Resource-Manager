import type { SourceSnapshot } from "../../frontendRuntime/source/SourceSnapshot";
import type { SystemEventEntry, SystemEventRaw, SystemEventsReport } from "../../data/freezeReports/systemEvents.ts";
import type {
  FreezeRecord, FreezeRecorderStatus, FreezeReviewDetails, FreezeReviewEvent, FreezeReviewSummary
} from "../../data/freezeReports/freezeRecorder.ts";
import type { FreezeRecorderConfig } from "../../data/freezeReports/freezeSettings.ts";
import { formatBytes } from "../../presentation/byteUnits.ts";
import { userFacingDateTime, userFacingOptionalValue } from "../../presentation/userFacingText.ts";
import { compactUserDetailSections, userDetailItem, userDetailSection, type UserDetailSection } from "../../presentation/userDetails.ts";
import { uiText } from "../../text.ts";

/* Localize backend values, codes, and UTC timestamps; preserve missing data instead of substituting zero. */

export interface LabelledValue {
  readonly label: string;
  readonly value: string;
}

export type ContentStateView = { readonly kind: "waiting" | "unavailable" | "empty"; readonly title: string; readonly detail?: string };

/** Format UTC milliseconds as local date and time. */
export function freezeTime(ms: number | null | undefined): string {
  return ms == null ? userFacingOptionalValue(null) : userFacingDateTime(new Date(ms).toISOString());
}

/** Use the shared report-duration wording. */
export function freezeDuration(ms: number | null | undefined): string {
  if (ms == null) return userFacingOptionalValue(null);
  const seconds = ms / 1000;
  const copy = uiText.optimizationReport.duration;
  if (seconds >= 86400) return copy.days(Math.round(seconds / 86400));
  if (seconds >= 3600) return copy.hours(Math.round(seconds / 3600));
  if (seconds >= 60) return copy.minutes(Math.round(seconds / 60));
  return copy.seconds(Math.max(1, Math.round(seconds)));
}

export function bugcheckText(code: number | null | undefined): string {
  return code == null ? userFacingOptionalValue(null) : `0x${code.toString(16).toUpperCase().padStart(8, "0")}`;
}

function trimNumber(value: number, digits: number) {
  return Number(value.toFixed(digits)).toString();
}

// System event reports.

export function systemEventsStateView(snapshot: SourceSnapshot<SystemEventsReport>): ContentStateView | null {
  const copy = uiText.freezeReports.systemEvents;
  if (snapshot.status === "error" || snapshot.status === "unavailable" || snapshot.status === "disposed") return { kind: "unavailable", title: copy.readFailed };
  const report = snapshot.data;
  if (!report) return { kind: "waiting", title: copy.loading };
  if (report.items.length > 0) return null;
  switch (report.status) {
    case "not-observed": return { kind: "unavailable", title: copy.notObservedTitle, detail: copy.notObservedDetail };
    case "unavailable": return { kind: "unavailable", title: copy.unavailableTitle, detail: copy.unavailableDetail };
    case "observing": return { kind: "empty", title: copy.emptyTitle, detail: copy.emptyDetail };
  }
}

/** Keep summary geometry fixed while loading; show dropped records separately. */
export function systemEventsSummary(report: SystemEventsReport | null): LabelledValue[] {
  const copy = uiText.freezeReports.systemEvents;
  const placeholder = userFacingOptionalValue(null);
  const active = report?.items.filter((item) => item.state === "active").length;
  return [
    { label: copy.status.label, value: report ? copy.status[report.status] : placeholder },
    { label: copy.activeCount, value: active === undefined ? placeholder : String(active) },
    { label: copy.historyCount, value: report ? String(report.items.length - active!) : placeholder }
  ];
}

export function systemEventTargetText(entry: { readonly category: string; readonly target: string }): string {
  if (entry.target) return entry.target;
  return uiText.freezeReports.systemEvents.machineTarget;
}

export function systemEventEntryFacts(entry: SystemEventEntry): LabelledValue[] {
  const copy = uiText.freezeReports.systemEvents;
  return [
    { label: copy.count, value: copy.times(entry.count) },
    { label: copy.firstObserved, value: freezeTime(entry.firstObservedAt) },
    { label: copy.lastObserved, value: freezeTime(entry.lastObservedAt) }
  ];
}

export function systemEventEntryDetails(entry: SystemEventEntry): { title: string; sections: UserDetailSection[] } {
  const copy = uiText.freezeReports.systemEvents;
  return {
    title: `${copy.category[entry.category]} · ${systemEventTargetText(entry)}`,
    sections: compactUserDetailSections([
      userDetailSection(copy.overview, [
        userDetailItem(copy.categoryLabel, copy.category[entry.category]),
        userDetailItem(copy.target, systemEventTargetText(entry)),
        userDetailItem(copy.state, entry.state === "active" ? copy.activeEntry : copy.historyEntry),
        userDetailItem(copy.count, copy.times(entry.count)),
        userDetailItem(copy.firstObserved, freezeTime(entry.firstObservedAt)),
        userDetailItem(copy.lastObserved, freezeTime(entry.lastObservedAt))
      ])
    ])
  };
}

/** Display raw event fields separately from XML. */
export function systemEventRawFacts(raw: SystemEventRaw): LabelledValue[] {
  const copy = uiText.freezeReports.systemEvents;
  return [
    { label: copy.time, value: freezeTime(raw.at) },
    { label: copy.channel, value: copy.channelName[raw.channel] },
    { label: copy.provider, value: raw.provider },
    { label: copy.eventId, value: String(raw.eventId) },
    { label: copy.recordId, value: String(raw.recordId) },
    ...(raw.bugcheckCode === null ? [] : [{ label: copy.bugcheckCode, value: bugcheckText(raw.bugcheckCode) }]),
    ...(raw.dumpPath === null ? [] : [{ label: copy.dumpPath, value: raw.dumpPath }])
  ];
}

/** Order raw events newest first. */
export function systemEventsNewestFirst<T extends { readonly at: number }>(events: readonly T[]): T[] {
  return [...events].sort((left, right) => right.at - left.at);
}

// Freeze recording and startup reviews.

export function freezeRecorderStateView(snapshot: SourceSnapshot<FreezeRecorderStatus>): ContentStateView | null {
  const copy = uiText.freezeReports.recorder;
  if (snapshot.status === "error" || snapshot.status === "unavailable" || snapshot.status === "disposed") return { kind: "unavailable", title: copy.readFailed };
  const status = snapshot.data;
  if (!status) return { kind: "waiting", title: copy.loading };
  if (status.reviews.length > 0) return null;
  return { kind: "empty", title: copy.emptyTitle, detail: copy.emptyDetail };
}

export function freezeRecorderSummary(status: FreezeRecorderStatus | null): LabelledValue[] {
  const copy = uiText.freezeReports.recorder;
  const placeholder = userFacingOptionalValue(null);
  return [
    { label: copy.recorder, value: status ? status.running ? copy.running : copy.stopped : placeholder },
    { label: copy.lastRecordAt, value: status ? freezeTime(status.lastRecordAt) : placeholder },
    { label: copy.reviewCount, value: status ? String(status.reviews.length) : placeholder }
  ];
}

export function freezeReviewTitle(review: { readonly shutdownAt: number | null; readonly lastRecordAt: number }): string {
  const copy = uiText.freezeReports.recorder;
  return copy.reviewTitle(freezeTime(review.shutdownAt ?? review.lastRecordAt));
}

export function freezeReviewFacts(review: FreezeReviewSummary): LabelledValue[] {
  const copy = uiText.freezeReports.recorder;
  return [
    { label: copy.lastRecordAt, value: freezeTime(review.lastRecordAt) },
    { label: copy.shutdownAt, value: freezeTime(review.shutdownAt) },
    { label: copy.gapDuration, value: freezeDuration(review.gapMs) },
    ...(review.bugcheckCode === null ? [] : [{ label: copy.bugcheckCode, value: bugcheckText(review.bugcheckCode) }])
  ];
}

/** Keep labels and geometry while details load. */
export function freezeReviewOverview(details: FreezeReviewDetails | null): UserDetailSection[] {
  const copy = uiText.freezeReports.recorder;
  const detail = copy.details;
  const placeholder = userFacingOptionalValue(null);
  const value = (read: (details: FreezeReviewDetails) => string) => details ? read(details) : placeholder;
  return compactUserDetailSections([
    userDetailSection(detail.overview, [
      userDetailItem(copy.lastRecordAt, value((d) => freezeTime(d.review.lastRecordAt))),
      userDetailItem(copy.shutdownAt, value((d) => freezeTime(d.review.shutdownAt))),
      userDetailItem(copy.gapDuration, value((d) => freezeDuration(d.review.gapMs))),
      userDetailItem(detail.reviewWindow, value((d) => detail.timeRange(freezeTime(d.review.reviewFromMs), freezeTime(d.review.reviewToMs)))),
      userDetailItem(detail.samplesSeen, value((d) => String(d.review.samplesSeen))),
      userDetailItem(detail.inputsComplete, value((d) => d.review.inputsComplete ? detail.yes : detail.no)),
      userDetailItem(copy.bugcheckCode, value((d) => bugcheckText(d.review.bugcheckCode))),
      userDetailItem(detail.dumpPath, value((d) => userFacingOptionalValue(d.dumpPath)))
    ]),
    userDetailSection(detail.thresholdsTitle, freezeThresholdItems(details?.thresholds ?? null))
  ]);
}

function freezeThresholdItems(config: FreezeRecorderConfig | null) {
  const detail = uiText.freezeReports.recorder.details;
  const placeholder = userFacingOptionalValue(null);
  const value = (read: (config: FreezeRecorderConfig) => string) => config ? read(config) : placeholder;
  return [
    userDetailItem(detail.commitImmediatePercent, value((c) => c.commitImmediatePercent === null
      ? placeholder : `${trimNumber(c.commitImmediatePercent, 2)}%`)),
    userDetailItem(detail.physicalAvailablePercent, value((c) => `${trimNumber(c.physicalAvailablePercent, 2)}%`)),
    userDetailItem(detail.nonpagedPoolPercent, value((c) => `${trimNumber(c.nonpagedPoolPercent, 2)}%`)),
    userDetailItem(detail.storageConsecutive, value((c) => uiText.freezeReports.systemEvents.times(c.storageUnresponsiveConsecutive))),
    userDetailItem(detail.sampleInterval, value((c) => freezeDuration(c.sampleIntervalMs))),
    userDetailItem(detail.reviewWindowLength, value((c) => freezeDuration(c.reviewWindowMs)))
  ];
}

/** Represent missing curve samples as null gaps. */
export interface FreezeCurve {
  readonly id: string;
  readonly label: string;
  readonly points: readonly { readonly at: number; readonly value: number | null }[];
  readonly format: (value: number) => string;
}

function maximum(values: Array<number | null>): number | null {
  const present = values.filter((value): value is number => value !== null);
  return present.length ? Math.max(...present) : null;
}

export function freezeCurves(records: readonly FreezeRecord[]): FreezeCurve[] {
  const copy = uiText.freezeReports.recorder.details.curve;
  const percent = (value: number) => `${trimNumber(value, 1)}%`;
  const memory = (value: number) => formatBytes(value, "memory");
  const curve = (id: string, label: string, value: (record: FreezeRecord) => number | null, format: (value: number) => string): FreezeCurve =>
    ({ id, label, format, points: records.map((record) => ({ at: record.timestampMs, value: value(record) })) });
  return [
    curve("commit", copy.commit, (record) => record.commitBytes === null || !record.commitLimitBytes
      ? null : record.commitBytes / record.commitLimitBytes * 100, percent),
    curve("physical-available", copy.physicalAvailable, (record) => record.physicalAvailableBytes === null || !record.physicalTotalBytes
      ? null : record.physicalAvailableBytes / record.physicalTotalBytes * 100, percent),
    curve("nonpaged-pool", copy.nonpagedPool, (record) => record.nonpagedPoolBytes, memory),
    curve("paged-pool", copy.pagedPool, (record) => record.pagedPoolBytes, memory),
    curve("disk-queue", copy.diskQueue, (record) => maximum(record.disks.map((disk) => disk.queueDepth)), (value) => trimNumber(value, 1)),
    curve("disk-latency", copy.diskLatency, (record) => maximum(record.disks.map((disk) => disk.latencyMs)), (value) => `${trimNumber(value, 1)} ms`),
    curve("interrupt", copy.interrupt, (record) => record.interruptCpuPercent, percent)
  ];
}

export function freezeCurveRange(curve: FreezeCurve): { readonly min: number; readonly max: number } | null {
  const values = curve.points.map((point) => point.value).filter((value): value is number => value !== null);
  return values.length ? { min: Math.min(...values), max: Math.max(...values) } : null;
}

/** Show the final memory, pool, interrupt, disk, and process snapshot. */
export function freezeLastRecordSections(record: FreezeRecord | undefined): UserDetailSection[] {
  if (!record) return [];
  const detail = uiText.freezeReports.recorder.details;
  const bytes = (value: number | null) => value === null ? userFacingOptionalValue(null) : formatBytes(value, "memory");
  const optional = (value: number | null, format: (value: number) => string) => value === null ? userFacingOptionalValue(null) : format(value);
  return compactUserDetailSections([
    userDetailSection(detail.lastRecordTitle, [
      userDetailItem(detail.time, freezeTime(record.timestampMs)),
      userDetailItem(detail.commit, detail.ofLimit(bytes(record.commitBytes), bytes(record.commitLimitBytes))),
      userDetailItem(detail.physicalAvailable, detail.ofLimit(bytes(record.physicalAvailableBytes), bytes(record.physicalTotalBytes))),
      userDetailItem(detail.curve.pagedPool, bytes(record.pagedPoolBytes)),
      userDetailItem(detail.curve.nonpagedPool, bytes(record.nonpagedPoolBytes)),
      userDetailItem(detail.curve.interrupt, optional(record.interruptCpuPercent, (value) => `${trimNumber(value, 1)}%`)),
      userDetailItem(detail.longestInterrupt, optional(record.longestInterruptUs, (value) => `${trimNumber(value, 0)} µs`)),
      userDetailItem(detail.kernelLeakActive, record.kernelLeakActive === null ? userFacingOptionalValue(null)
        : record.kernelLeakActive ? detail.yes : detail.no)
    ]),
    userDetailSection(detail.processesTitle, record.processes.map((process) =>
      userDetailItem(detail.processName(process.imageName || userFacingOptionalValue(null), String(process.pid)), formatBytes(process.privateCommitBytes, "memory")))),
    ...record.disks.map((disk) => userDetailSection(detail.diskName(String(disk.physicalDiskId)), [
      userDetailItem(detail.queueDepth, optional(disk.queueDepth, (value) => trimNumber(value, 1))),
      userDetailItem(detail.latency, optional(disk.latencyMs, (value) => `${trimNumber(value, 1)} ms`)),
      userDetailItem(detail.completedOperations, optional(disk.completedOperations, (value) => trimNumber(value, 0)))
    ]))
  ]);
}

export function freezeReviewEventTitle(event: FreezeReviewEvent): string {
  const copy = uiText.freezeReports.systemEvents;
  return `${copy.category[event.category]} · ${event.target || copy.targetKind[event.targetKind]}`;
}

export function freezeReviewEventFacts(event: FreezeReviewEvent): LabelledValue[] {
  const copy = uiText.freezeReports;
  return [
    { label: copy.systemEvents.targetKindLabel, value: copy.systemEvents.targetKind[event.targetKind] },
    ...systemEventRawFacts(event.raw),
    ...(event.actualShutdownAt === null ? [] : [{ label: copy.recorder.details.actualShutdownAt, value: freezeTime(event.actualShutdownAt) }])
  ];
}
