import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { RequestProblem } from "../../frontendRuntime/request/RequestProblem.ts";
import {
  defineResponseDecoder, requireArray, requireBoolean, requireFiniteNumber,
  requireNonEmptyString, requireNonNegativeSafeInteger, requireNullable, requireOneOf, requireRecord,
  requireString, requireStringArray, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import { uiText } from "../../text.ts";
import {
  itemKinds, itemUses, syncPrecisions,
  type CatalogEntry, type ItemKind, type SelectionItem, type SyncPrecision
} from "../itemSelection/itemSelectionModel.ts";

export interface RecordingHeader {
  id: string;
  softwareId: string;
  softwareName: string;
  startedAt: string;
  endedAt: string | null;
  maximumDurationSeconds: number;
  status: "recording" | "completed";
  stopReason: string | null;
  incomplete: boolean;
  /* Optional recording metadata stays undefined when absent; omit its UI without fabricating values. */
  /** Requested profile, or null for explicit items. */
  profileId?: string | null;
  profileName?: string | null;
  /** Snapshot items at start and add effective sources, intervals, and gaps at completion. */
  items?: RecordingItemReport[];
  processes?: RecordingProcess[];
  storedBytes?: number;
  /** Recording report cap in KiB; zero means no cap was recorded. */
  maximumReportKib?: number;
}

export interface RecordingGap {
  fromMs: number;
  toMs: number;
  /** Preserve source and reader reason codes. Generic item/value reasons apply only without a specific source reason; display unknown codes unchanged. */
  reason: string;
}

export interface RecordingItemReport {
  kind: ItemKind;
  id: string;
  unit: string;
  selectedSource: string;
  actualSources: string[];
  selectedIntervalMs: number | null;
  /** Preserve unknown interval-source codes. */
  intervalOrigin: string;
  actualIntervalMs: number | null;
  syncPrecision: SyncPrecision;
  pointCount: number;
  gaps: RecordingGap[];
}

export interface RecordingProcess {
  processId: number;
  processStartKey: number;
  name: string;
  firstSeenAt: string;
  exitedAt: string | null;
}

/** Registered running software from the targets endpoint. */
export interface RecordingTarget {
  softwareId: string;
  name: string;
  processCount: number;
}

export type RecordingItem = SelectionItem;

export interface RecordingProfile {
  id: string;
  name: string;
  defaultIntervalMs: number;
  items: RecordingItem[];
}

/** Match backend profile name, count, and item limits. */
export const maximumProfileNameLength = 64;
export const maximumProfiles = 64;
export const maximumProfileItems = 256;

export interface FrameInterval {
  processId: number;
  processStartKey: number;
  source: number;
  swapChain: number;
  endedAt: string;
  durationMs: number;
}

export interface RecordingSummary {
  intervalCount: number;
  durationMs: number;
  averageFps: number;
  onePercentLowFps: number;
  pointOnePercentLowFps: number;
  p50FrameTimeMs: number;
  p95FrameTimeMs: number;
  p99FrameTimeMs: number;
  maxFrameTimeMs: number;
}

export interface ResourceSample {
  capturedAt: string;
  software: Record<string, number | null>;
  system: Record<string, number | null>;
  processes: Array<{ processId: number; processStartKey: number }>;
}

export interface RecordingReport {
  recording: RecordingHeader;
  summary: RecordingSummary | null;
  frameIntervals: FrameInterval[];
  fpsDistribution: Array<{ fromFps: number; toFps: number; count: number }>;
  resourceSamples: ResourceSample[];
}

function nullableString(value: unknown, path: string): string | null {
  return value === null ? null : requireString(value, path);
}

function nullableInteger(value: unknown, path: string): number | null {
  return requireNullable(value, path, requireNonNegativeSafeInteger);
}

function header(value: unknown): RecordingHeader {
  const row = requireRecord(value);
  const status = requireString(row.status, "status");
  if (status !== "recording" && status !== "completed") {
    throw new ResponseDecodeError("status", "recording or completed");
  }
  const decoded: RecordingHeader = {
    id: requireNonEmptyString(row.id, "id"),
    softwareId: requireNonEmptyString(row.softwareId, "softwareId"),
    softwareName: requireString(row.softwareName, "softwareName"),
    startedAt: requireString(row.startedAt, "startedAt"),
    endedAt: nullableString(row.endedAt, "endedAt"),
    maximumDurationSeconds: requireNonNegativeSafeInteger(row.maximumDurationSeconds, "maximumDurationSeconds"),
    status,
    stopReason: nullableString(row.stopReason, "stopReason"),
    incomplete: requireBoolean(row.incomplete, "incomplete")
  };
  // Strictly decode optional fields when present; preserve absence.
  if ("profileId" in row) decoded.profileId = nullableString(row.profileId, "profileId");
  if ("profileName" in row) decoded.profileName = nullableString(row.profileName, "profileName");
  if ("items" in row) decoded.items = requireArray(row.items, "items").map(itemReport);
  if ("processes" in row) decoded.processes = requireArray(row.processes, "processes").map(processRow);
  if ("storedBytes" in row) decoded.storedBytes = requireNonNegativeSafeInteger(row.storedBytes, "storedBytes");
  if ("maximumReportKib" in row) {
    decoded.maximumReportKib = requireNonNegativeSafeInteger(row.maximumReportKib, "maximumReportKib");
  }
  return decoded;
}

function itemReport(value: unknown, index: number): RecordingItemReport {
  const path = `items[${index}]`;
  const row = requireRecord(value, path);
  return {
    kind: requireOneOf(row.kind, `${path}.kind`, itemKinds),
    id: requireNonEmptyString(row.id, `${path}.id`),
    unit: requireString(row.unit, `${path}.unit`),
    selectedSource: requireNonEmptyString(row.selectedSource, `${path}.selectedSource`),
    actualSources: requireStringArray(row.actualSources, `${path}.actualSources`),
    selectedIntervalMs: nullableInteger(row.selectedIntervalMs, `${path}.selectedIntervalMs`),
    intervalOrigin: requireNonEmptyString(row.intervalOrigin, `${path}.intervalOrigin`),
    actualIntervalMs: nullableInteger(row.actualIntervalMs, `${path}.actualIntervalMs`),
    syncPrecision: requireOneOf(row.syncPrecision, `${path}.syncPrecision`, syncPrecisions),
    pointCount: requireNonNegativeSafeInteger(row.pointCount, `${path}.pointCount`),
    gaps: requireArray(row.gaps, `${path}.gaps`).map((gap, gapIndex) => {
      const gapPath = `${path}.gaps[${gapIndex}]`;
      const entry = requireRecord(gap, gapPath);
      return {
        fromMs: requireFiniteNumber(entry.fromMs, `${gapPath}.fromMs`),
        toMs: requireFiniteNumber(entry.toMs, `${gapPath}.toMs`),
        reason: requireNonEmptyString(entry.reason, `${gapPath}.reason`)
      };
    })
  };
}

function processRow(value: unknown, index: number): RecordingProcess {
  const path = `processes[${index}]`;
  const row = requireRecord(value, path);
  return {
    processId: requireNonNegativeSafeInteger(row.processId, `${path}.processId`),
    processStartKey: requireFiniteNumber(row.processStartKey, `${path}.processStartKey`),
    name: requireString(row.name, `${path}.name`),
    firstSeenAt: requireString(row.firstSeenAt, `${path}.firstSeenAt`),
    exitedAt: nullableString(row.exitedAt, `${path}.exitedAt`)
  };
}

function recordingItem(value: unknown, path: string): RecordingItem {
  const row = requireRecord(value, path);
  return {
    kind: requireOneOf(row.kind, `${path}.kind`, itemKinds),
    id: requireNonEmptyString(row.id, `${path}.id`),
    source: requireNonEmptyString(row.source, `${path}.source`),
    intervalMs: nullableInteger(row.intervalMs, `${path}.intervalMs`),
    syncPrecision: requireOneOf(row.syncPrecision, `${path}.syncPrecision`, syncPrecisions)
  };
}

function profile(value: unknown, path: string): RecordingProfile {
  const row = requireRecord(value, path);
  return {
    id: requireNonEmptyString(row.id, `${path}.id`),
    name: requireString(row.name, `${path}.name`),
    defaultIntervalMs: requireNonNegativeSafeInteger(row.defaultIntervalMs, `${path}.defaultIntervalMs`),
    items: requireArray(row.items, `${path}.items`).map((item, index) => recordingItem(item, `${path}.items[${index}]`))
  };
}

function metricValues(value: unknown, path: string): Record<string, number | null> {
  const row = requireRecord(value, path);
  const values: Record<string, number | null> = {};
  for (const [key, item] of Object.entries(row)) {
    values[key] = item === null ? null : requireFiniteNumber(item, `${path}.${key}`);
  }
  return values;
}

export const recordingTargetsDecoder = defineResponseDecoder<RecordingTarget[]>("targeted.targets.v2", (value) =>
  requireArray(value, "$").map((item, index) => {
    const path = `$[${index}]`;
    const row = requireRecord(item, path);
    return {
      softwareId: requireNonEmptyString(row.softwareId, `${path}.softwareId`),
      name: requireString(row.name, `${path}.name`),
      processCount: requireNonNegativeSafeInteger(row.processCount, `${path}.processCount`)
    };
  }));

export const itemCatalogDecoder = defineResponseDecoder<CatalogEntry[]>("targeted.items.catalog.v1", (value) =>
  requireArray(value, "$").map((item, index) => {
    const path = `$[${index}]`;
    const row = requireRecord(item, path);
    return {
      kind: requireOneOf(row.kind, `${path}.kind`, itemKinds),
      id: requireNonEmptyString(row.id, `${path}.id`),
      unit: requireString(row.unit, `${path}.unit`),
      sources: requireStringArray(row.sources, `${path}.sources`),
      naturalIntervalMs: nullableInteger(row.naturalIntervalMs, `${path}.naturalIntervalMs`),
      // Filter picker items by recording or overlay use.
      uses: requireArray(row.uses, `${path}.uses`).map((use, useIndex) =>
        requireOneOf(use, `${path}.uses[${useIndex}]`, itemUses)),
      // Unavailable items retain the backend reason and produce a full-recording gap.
      unavailableReason: row.unavailableReason === undefined ? null
        : requireNullable(row.unavailableReason, `${path}.unavailableReason`, requireNonEmptyString)
    };
  }));

export const profileDecoder = defineResponseDecoder<RecordingProfile>(
  "targeted.profile.v1", (value) => profile(value, "$"));
export const profileListDecoder = defineResponseDecoder<RecordingProfile[]>("targeted.profiles.v1", (value) =>
  requireArray(value, "$").map((item, index) => profile(item, `$[${index}]`)));

export const recordingHeaderDecoder = defineResponseDecoder<RecordingHeader>("targeted.header.v2", header);
export const recordingListDecoder = defineResponseDecoder<RecordingHeader[]>(
  "targeted.list.v2", (value) => requireArray(value, "$").map(header));
export const recordingReportDecoder = defineResponseDecoder<RecordingReport>("targeted.report.v2", (value) => {
  const row = requireRecord(value);
  const summary = row.summary === null ? null : requireRecord(row.summary, "summary");
  return {
    recording: header(row.recording),
    summary: summary === null ? null : {
      intervalCount: requireNonNegativeSafeInteger(summary.intervalCount, "summary.intervalCount"),
      durationMs: requireFiniteNumber(summary.durationMs, "summary.durationMs"),
      averageFps: requireFiniteNumber(summary.averageFps, "summary.averageFps"),
      onePercentLowFps: requireFiniteNumber(summary.onePercentLowFps, "summary.onePercentLowFps"),
      pointOnePercentLowFps: requireFiniteNumber(summary.pointOnePercentLowFps, "summary.pointOnePercentLowFps"),
      p50FrameTimeMs: requireFiniteNumber(summary.p50FrameTimeMs, "summary.p50FrameTimeMs"),
      p95FrameTimeMs: requireFiniteNumber(summary.p95FrameTimeMs, "summary.p95FrameTimeMs"),
      p99FrameTimeMs: requireFiniteNumber(summary.p99FrameTimeMs, "summary.p99FrameTimeMs"),
      maxFrameTimeMs: requireFiniteNumber(summary.maxFrameTimeMs, "summary.maxFrameTimeMs")
    },
    frameIntervals: requireArray(row.frameIntervals, "frameIntervals").map((item) => {
      const frame = requireRecord(item);
      return {
        processId: requireNonNegativeSafeInteger(frame.processId, "frame.processId"),
        processStartKey: requireFiniteNumber(frame.processStartKey, "frame.processStartKey"),
        source: requireNonNegativeSafeInteger(frame.source, "frame.source"),
        swapChain: requireFiniteNumber(frame.swapChain, "frame.swapChain"),
        endedAt: requireString(frame.endedAt, "frame.endedAt"),
        durationMs: requireFiniteNumber(frame.durationMs, "frame.durationMs")
      };
    }),
    fpsDistribution: requireArray(row.fpsDistribution, "fpsDistribution").map((item) => {
      const bucket = requireRecord(item);
      return {
        fromFps: requireFiniteNumber(bucket.fromFps, "bucket.fromFps"),
        toFps: requireFiniteNumber(bucket.toFps, "bucket.toFps"),
        count: requireNonNegativeSafeInteger(bucket.count, "bucket.count")
      };
    }),
    resourceSamples: requireArray(row.resourceSamples, "resourceSamples").map((item) => {
      const sample = requireRecord(item);
      return {
        capturedAt: requireString(sample.capturedAt, "sample.capturedAt"),
        software: metricValues(sample.software, "sample.software"),
        system: metricValues(sample.system, "sample.system"),
        processes: requireArray(sample.processes, "sample.processes").map((value) => {
          const process = requireRecord(value);
          return {
            processId: requireNonNegativeSafeInteger(process.processId, "process.processId"),
            processStartKey: requireFiniteNumber(process.processStartKey, "process.processStartKey")
          };
        })
      };
    })
  };
});

/* HTTP failures preserve status and optional error codes. Without a code, explain status by action; non-HTTP failures retain the request-layer explanation. */
export interface RecordingFailure {
  status: number | null;
  code: string | null;
  /** Request-layer explanation for non-HTTP failures. */
  message: string;
}

export function recordingFailure(error: unknown): RecordingFailure {
  if (error instanceof RequestProblem) {
    const payload = error.payload;
    const code = payload !== null && typeof payload === "object"
      && typeof (payload as { error?: unknown }).error === "string"
      ? (payload as { error: string }).error : null;
    return { status: error.kind === "http" ? error.status ?? null : null, code, message: error.userMessage };
  }
  return { status: null, code: null, message: error instanceof Error ? error.message : String(error) };
}

/** Use start-specific wording for empty-body start failures. */
export type RecordingAction = "start" | "profile" | "other";

/** Map backend recording and profile error codes to localization keys. */
const errorCodes = {
  "target-not-running": "targetNotRunning",
  "report-size-limit-exceeded": "reportSizeLimitExceeded",
  "targeted-recording-profile-not-found": "profileNotFound",
  "targeted-recording-profile-invalid-id": "profileInvalidId",
  "targeted-recording-profile-invalid-name": "profileInvalidName",
  "targeted-recording-profile-duplicate-id": "profileDuplicateId",
  "targeted-recording-profile-items-empty": "profileItemsEmpty",
  "targeted-recording-profile-items-too-many": "profileItemsTooMany",
  "targeted-recording-profiles-empty": "profilesEmpty",
  "targeted-recording-profiles-too-many": "profilesTooMany",
  "targeted-recording-last-profile": "lastProfile",
  "targeted-recording-item-unknown": "itemUnknown",
  "targeted-recording-item-duplicate": "itemDuplicate",
  "targeted-recording-item-invalid-kind": "itemInvalidKind",
  "targeted-recording-item-invalid-precision": "itemInvalidPrecision",
  "targeted-recording-item-source-invalid": "itemSourceInvalid",
  "targeted-recording-interval-out-of-range": "intervalOutOfRange",
  "settings-revision-conflict": "settingsConflict"
} as const;

export type RecordingErrorKey = typeof errorCodes[keyof typeof errorCodes];

export function recordingErrorKey(code: string): RecordingErrorKey | null {
  return Object.prototype.hasOwnProperty.call(errorCodes, code)
    ? errorCodes[code as keyof typeof errorCodes] : null;
}

/** Display unknown error codes unchanged. */
export function recordingErrorText(failure: RecordingFailure, action: RecordingAction): string {
  const copy = uiText.targetedReport.errors;
  if (failure.code !== null) {
    const key = recordingErrorKey(failure.code);
    return key === null ? copy.unknown(failure.code) : copy[key];
  }
  if (failure.status === null) return failure.message || uiText.targetedReport.failed;
  if (action === "start") {
    if (failure.status === 404) return copy.softwareNotFound;
    if (failure.status === 409) return uiText.targetedReport.oneAtATime;
    if (failure.status === 400) return copy.invalidRequest;
  }
  return uiText.targetedReport.failed;
}

type Client = Pick<RequestClient, "request">;
const error = () => uiText.targetedReport.failed;
const jsonHeaders = { "Content-Type": "application/json" };

export function getRecordingTargets(client: Client, signal?: AbortSignal) {
  return client.request({ key: "targeted.targets", url: "/api/targeted-recordings/targets",
    fallbackError: error(), decoder: recordingTargetsDecoder, signal });
}

export function getItemCatalog(client: Client, signal?: AbortSignal) {
  return client.request({ key: "targeted.items.catalog", url: "/api/targeted-recordings/items/catalog",
    fallbackError: error(), decoder: itemCatalogDecoder, signal });
}

export function listProfiles(client: Client, signal?: AbortSignal) {
  return client.request({ key: "targeted.profiles", url: "/api/targeted-recordings/profiles",
    fallbackError: error(), decoder: profileListDecoder, signal });
}

/** Create by copying a profile or supplying its interval and items. */
export type ProfileCreation =
  | { name: string; copyFromId: string }
  | { name: string; defaultIntervalMs: number; items: RecordingItem[] };

export function createProfile(client: Client, creation: ProfileCreation) {
  return client.request({ key: "targeted.profiles.create", url: "/api/targeted-recordings/profiles",
    fallbackError: error(), decoder: profileDecoder,
    request: { method: "POST", headers: jsonHeaders, body: JSON.stringify(creation) } });
}

export interface ProfilePatch {
  name?: string;
  defaultIntervalMs?: number;
  items?: RecordingItem[];
}

export function patchProfile(client: Client, id: string, patch: ProfilePatch) {
  return client.request({ key: "targeted.profiles.patch",
    url: `/api/targeted-recordings/profiles/${encodeURIComponent(id)}`,
    fallbackError: error(), decoder: profileDecoder,
    request: { method: "PATCH", headers: jsonHeaders, body: JSON.stringify(patch) } });
}

export function deleteProfile(client: Client, id: string) {
  return client.request({ key: "targeted.profiles.delete",
    url: `/api/targeted-recordings/profiles/${encodeURIComponent(id)}`,
    fallbackError: error(), decoder: defineResponseDecoder<null>("targeted.empty.v1", () => null),
    request: { method: "DELETE", allowEmptyResponse: true } });
}

export function listRecordings(client: Client, signal?: AbortSignal) {
  return client.request({ key: "targeted.list", url: "/api/targeted-recordings",
    fallbackError: error(), decoder: recordingListDecoder, signal });
}

/** Send either profileId or items; explicit items take precedence at the backend. */
export type RecordingStart =
  | { softwareId: string; profileId: string; maximumDurationSeconds: number }
  | { softwareId: string; items: RecordingItem[]; maximumDurationSeconds: number };

export function startRecording(client: Client, start: RecordingStart) {
  return client.request({ key: "targeted.start", url: "/api/targeted-recordings",
    fallbackError: error(), decoder: recordingHeaderDecoder,
    request: { method: "POST", headers: jsonHeaders, body: JSON.stringify(start) } });
}

export function stopRecording(client: Client, id: string) {
  return client.request({ key: "targeted.stop", url: `/api/targeted-recordings/${encodeURIComponent(id)}/stop`,
    fallbackError: error(), decoder: recordingHeaderDecoder, request: { method: "POST" } });
}

export function getRecording(client: Client, id: string, signal?: AbortSignal) {
  return client.request({ key: "targeted.get", url: `/api/targeted-recordings/${encodeURIComponent(id)}`,
    fallbackError: error(), decoder: recordingReportDecoder, signal, timeoutMs: 60_000 });
}

export function deleteRecording(client: Client, id: string) {
  return client.request({ key: "targeted.delete", url: `/api/targeted-recordings/${encodeURIComponent(id)}`,
    fallbackError: error(), decoder: defineResponseDecoder<null>("targeted.empty.v1", () => null),
    request: { method: "DELETE", allowEmptyResponse: true } });
}

const bytesDecoder = defineResponseDecoder<ArrayBuffer>("targeted.export.v1", (value) => {
  if (!(value instanceof ArrayBuffer)) {
    throw new ResponseDecodeError("$", "binary export");
  }
  return value;
});

/** Supported backend export formats. */
export const exportFormats = ["presentmon", "long-csv", "frames-csv", "json", "csv"] as const;
export type ExportFormat = typeof exportFormats[number];

/** Match the backend Content-Disposition filename. */
export function exportFileName(id: string, format: ExportFormat) {
  switch (format) {
    case "json": return `targeted-recording-${id}.json`;
    case "csv": return `targeted-recording-${id}.csv`;
    case "long-csv": return `targeted-recording-${id}-long.csv`;
    case "frames-csv": return `targeted-recording-${id}-frames.csv`;
    case "presentmon": return `targeted-recording-${id}-presentmon-1.6.csv`;
  }
}

export function exportRecording(client: Client, id: string, format: ExportFormat) {
  return client.request({ key: `targeted.export.${format}`,
    url: `/api/targeted-recordings/${encodeURIComponent(id)}/export?format=${format}`,
    fallbackError: error(), decoder: bytesDecoder, timeoutMs: 60_000,
    request: { binary: true } });
}
