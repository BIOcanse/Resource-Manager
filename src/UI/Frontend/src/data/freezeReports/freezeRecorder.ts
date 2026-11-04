import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireNonEmptyString,
  requireNonNegativeFiniteNumber,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireSafeInteger,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import { uiText } from "../../text.ts";
import { decodeFreezeRecorderConfig, type FreezeRecorderConfig } from "./freezeSettings.ts";
import { decodeSystemEventRaw, requireSchema, systemEventRuleCategories, systemEventTargetKinds,
  type SystemEventRaw, type SystemEventRuleCategory, type SystemEventTargetKind } from "./systemEvents.ts";

/* Recorder snapshots carry status and immutable reviews. Missing metrics stay null; time is UTC Unix milliseconds and bytes are unsigned. Unknown tags, gaps and enums fail decoding. */

export function buildFreezeRecorderSubscriptionUrl(): string {
  return "/api/freeze-recorder/subscribe";
}

// Match backend gap names, including no-sample.
export const freezeRecorderGaps = ["no-sample", "interrupt-cpu-share", "longest-interrupt", "active-kernel-leak-system-item",
  "process-private-commit", "physical-disk-inventory", "physical-disk-completed-operations", "commit", "physical-memory", "kernel-pools"] as const;
export type FreezeRecorderGap = typeof freezeRecorderGaps[number];
// Wire tags use hyphens; review.tags uses equivalent underscore-named booleans.
export const freezeReviewTags = ["bugcheck", "commit-exhausted", "physical-exhausted", "pool-high", "storage-unresponsive",
  "gpu-reset", "hardware-error", "recording-stopped-normal"] as const;
export type FreezeReviewTag = typeof freezeReviewTags[number];

export interface FreezeReviewSummary {
  readonly id: string;
  readonly category: "unexpected-shutdown";
  readonly state: "history";
  readonly detailsRevision: number;
  readonly lastRecordAt: number;
  readonly shutdownAt: number | null;
  readonly gapMs: number | null;
  readonly tags: readonly FreezeReviewTag[];
  readonly inputsComplete: boolean;
  readonly bugcheckCode: number | null;
}

export interface FreezeRecorderStatus {
  readonly running: boolean;
  readonly lastRecordAt: number | null;
  readonly failure: string | null;
  readonly gaps: readonly FreezeRecorderGap[];
  readonly reviews: readonly FreezeReviewSummary[];
}

export const freezeRecorderStatusDecoder = defineResponseDecoder<FreezeRecorderStatus>("freeze-recorder.status.v1", (value) => {
  const record = requireRecord(value);
  requireSchema(record, "$", "freeze-recorder");
  return Object.freeze({
    running: requireBoolean(record.running, "$.running"),
    lastRecordAt: requireNullable(record.lastRecordAt, "$.lastRecordAt", requireSafeInteger),
    failure: requireNullable(record.failure, "$.failure", requireNonEmptyString),
    gaps: decodeUniqueCodes(record.gaps, "$.gaps", freezeRecorderGaps),
    reviews: decodeReviewSummaries(record.reviews, "$.reviews")
  });
});

export const freezeReviewListDecoder = defineResponseDecoder<readonly FreezeReviewSummary[]>("freeze-recorder.reviews.v1", (value) => {
  const record = requireRecord(value);
  requireSchema(record, "$", "freeze-reviews");
  return decodeReviewSummaries(record.items, "$.items");
});

function decodeReviewSummaries(value: unknown, path: string): readonly FreezeReviewSummary[] {
  const ids = new Set<string>();
  return Object.freeze(requireArray(value, path).map((item, index) => {
    const summary = decodeReviewSummary(item, `${path}[${index}]`);
    if (ids.has(summary.id)) throw new ResponseDecodeError(`${path}[${index}].id`, "a unique review id");
    ids.add(summary.id);
    return summary;
  }));
}

function decodeReviewSummary(value: unknown, path: string): FreezeReviewSummary {
  const record = requireRecord(value, path);
  return Object.freeze({
    id: requireReviewId(record.id, `${path}.id`),
    category: requireOneOf(record.category, `${path}.category`, ["unexpected-shutdown"] as const),
    state: requireOneOf(record.state, `${path}.state`, ["history"] as const),
    detailsRevision: requireNonNegativeSafeInteger(record.detailsRevision, `${path}.detailsRevision`),
    lastRecordAt: requireSafeInteger(record.lastRecordAt, `${path}.lastRecordAt`),
    shutdownAt: requireNullable(record.shutdownAt, `${path}.shutdownAt`, requireSafeInteger),
    gapMs: requireNullable(record.gapMs, `${path}.gapMs`, requireNonNegativeSafeInteger),
    tags: decodeUniqueCodes(record.tags, `${path}.tags`, freezeReviewTags),
    inputsComplete: requireBoolean(record.inputsComplete, `${path}.inputsComplete`),
    bugcheckCode: requireNullable(record.bugcheckCode, `${path}.bugcheckCode`, requireNonNegativeSafeInteger)
  });
}

function requireReviewId(value: unknown, path: string): string {
  const id = requireString(value, path);
  if (!/^[0-9a-f]{16}$/.test(id)) throw new ResponseDecodeError(path, "16 lowercase hexadecimal characters");
  return id;
}

function decodeUniqueCodes<const T extends string>(value: unknown, path: string, allowed: readonly T[]): readonly T[] {
  const codes = requireArray(value, path).map((item, index) => requireOneOf(item, `${path}[${index}]`, allowed));
  if (new Set(codes).size !== codes.length) throw new ResponseDecodeError(path, "unique codes");
  return Object.freeze(codes);
}

export interface FreezeRecordDisk {
  readonly physicalDiskId: number;
  readonly queueDepth: number | null;
  readonly latencyMs: number | null;
  readonly completedOperations: number | null;
}

export interface FreezeRecordProcess {
  readonly pid: number;
  readonly imageName: string;
  readonly privateCommitBytes: number;
}

/** Decode one fixed-capacity sample, retaining only the counted disks and processes. */
export interface FreezeRecord {
  readonly sequence: number;
  readonly timestampMs: number;
  readonly commitBytes: number | null;
  readonly commitLimitBytes: number | null;
  readonly physicalTotalBytes: number | null;
  readonly physicalAvailableBytes: number | null;
  readonly pagedPoolBytes: number | null;
  readonly nonpagedPoolBytes: number | null;
  readonly interruptCpuPercent: number | null;
  readonly longestInterruptUs: number | null;
  readonly kernelLeakActive: boolean | null;
  readonly disks: readonly FreezeRecordDisk[];
  readonly disksComplete: boolean;
  readonly processes: readonly FreezeRecordProcess[];
  readonly processesComplete: boolean;
}

export interface FreezeReviewEvent {
  readonly category: SystemEventRuleCategory;
  readonly targetKind: SystemEventTargetKind;
  readonly target: string;
  readonly raw: SystemEventRaw;
  readonly actualShutdownAt: number | null;
}

export interface FreezeReviewResult {
  readonly unexpectedShutdown: boolean;
  readonly lastRecordAt: number;
  readonly shutdownAt: number | null;
  readonly gapMs: number | null;
  readonly reviewFromMs: number;
  readonly reviewToMs: number;
  readonly bugcheckCode: number | null;
  readonly samplesSeen: number;
  readonly inputsComplete: boolean;
}

export interface FreezeReviewDetails {
  readonly id: string;
  readonly detailsRevision: number;
  readonly review: FreezeReviewResult;
  readonly tags: readonly FreezeReviewTag[];
  readonly records: readonly FreezeRecord[];
  readonly events: readonly FreezeReviewEvent[];
  readonly dumpPath: string | null;
  readonly thresholds: FreezeRecorderConfig;
}

// Backend capacities: 16 disks, 3 processes and 64 image-name bytes per sample.
const diskCapacity = 16;
const processCapacity = 3;

export const freezeReviewDetailsDecoder = defineResponseDecoder<FreezeReviewDetails>("freeze-recorder.review-details.v1", (value) => {
  const record = requireRecord(value);
  if (record.schemaVersion !== 1) throw new ResponseDecodeError("$.schemaVersion", "1");
  requireOneOf(record.category, "$.category", ["unexpected-shutdown"] as const);
  requireOneOf(record.state, "$.state", ["history"] as const);
  return Object.freeze({
    id: requireReviewId(record.id, "$.id"),
    detailsRevision: requireNonNegativeSafeInteger(record.detailsRevision, "$.detailsRevision"),
    review: decodeReviewResult(record.review, "$.review"),
    tags: decodeUniqueCodes(record.tags, "$.tags", freezeReviewTags),
    records: Object.freeze(requireArray(record.records, "$.records").map((item, index) => decodeRecord(item, `$.records[${index}]`))),
    events: Object.freeze(requireArray(record.events, "$.events").map((item, index) => decodeReviewEvent(item, `$.events[${index}]`))),
    dumpPath: requireNullable(record.dumpPath, "$.dumpPath", requireNonEmptyString),
    thresholds: decodeFreezeRecorderConfig(record.thresholds, "$.thresholds")
  });
});

function decodeReviewResult(value: unknown, path: string): FreezeReviewResult {
  const record = requireRecord(value, path);
  const tags = requireRecord(record.tags, `${path}.tags`);
  for (const tag of freezeReviewTags) requireBoolean(tags[tag.replaceAll("-", "_")], `${path}.tags.${tag.replaceAll("-", "_")}`);
  return Object.freeze({
    unexpectedShutdown: requireBoolean(record.unexpected_shutdown, `${path}.unexpected_shutdown`),
    lastRecordAt: requireSafeInteger(record.last_record_timestamp_ms, `${path}.last_record_timestamp_ms`),
    shutdownAt: requireNullable(record.shutdown_timestamp_ms, `${path}.shutdown_timestamp_ms`, requireSafeInteger),
    gapMs: requireNullable(record.gap_ms, `${path}.gap_ms`, requireNonNegativeSafeInteger),
    reviewFromMs: requireSafeInteger(record.review_from_ms, `${path}.review_from_ms`),
    reviewToMs: requireSafeInteger(record.review_to_ms, `${path}.review_to_ms`),
    bugcheckCode: requireNullable(record.bugcheck_code, `${path}.bugcheck_code`, requireNonNegativeSafeInteger),
    samplesSeen: requireNonNegativeSafeInteger(record.samples_seen, `${path}.samples_seen`),
    inputsComplete: requireBoolean(record.inputs_complete, `${path}.inputs_complete`)
  });
}

const nullableBytes = (value: unknown, path: string) => requireNullable(value, path, requireNonNegativeFiniteNumber);

function decodeRecord(value: unknown, path: string): FreezeRecord {
  const record = requireRecord(value, path);
  const sample = requireRecord(record.sample, `${path}.sample`);
  const at = `${path}.sample`;
  const diskCount = requireCount(sample.disk_count, `${at}.disk_count`, diskCapacity);
  const processCount = requireCount(sample.process_count, `${at}.process_count`, processCapacity);
  const disks = requireArray(sample.disks, `${at}.disks`);
  const processes = requireArray(sample.top_processes, `${at}.top_processes`);
  if (disks.length < diskCount) throw new ResponseDecodeError(`${at}.disks`, `at least disk_count (${diskCount}) entries`);
  if (processes.length < processCount) throw new ResponseDecodeError(`${at}.top_processes`, `at least process_count (${processCount}) entries`);
  return Object.freeze({
    sequence: requireNonNegativeSafeInteger(record.sequence, `${path}.sequence`),
    timestampMs: requireSafeInteger(sample.timestamp_ms, `${at}.timestamp_ms`),
    commitBytes: nullableBytes(sample.commit_bytes, `${at}.commit_bytes`),
    commitLimitBytes: nullableBytes(sample.commit_limit_bytes, `${at}.commit_limit_bytes`),
    physicalTotalBytes: nullableBytes(sample.physical_total_bytes, `${at}.physical_total_bytes`),
    physicalAvailableBytes: nullableBytes(sample.physical_available_bytes, `${at}.physical_available_bytes`),
    pagedPoolBytes: nullableBytes(sample.paged_pool_bytes, `${at}.paged_pool_bytes`),
    nonpagedPoolBytes: nullableBytes(sample.nonpaged_pool_bytes, `${at}.nonpaged_pool_bytes`),
    interruptCpuPercent: requireNullable(sample.interrupt_cpu_percent, `${at}.interrupt_cpu_percent`, requireNonNegativeFiniteNumber),
    longestInterruptUs: requireNullable(sample.longest_interrupt_us, `${at}.longest_interrupt_us`, requireNonNegativeFiniteNumber),
    kernelLeakActive: requireNullable(sample.kernel_leak_active, `${at}.kernel_leak_active`, requireBoolean),
    disks: Object.freeze(disks.slice(0, diskCount).map((item, index) => decodeDisk(item, `${at}.disks[${index}]`))),
    disksComplete: requireBoolean(sample.disks_complete, `${at}.disks_complete`),
    processes: Object.freeze(processes.slice(0, processCount).map((item, index) => decodeProcess(item, `${at}.top_processes[${index}]`))),
    processesComplete: requireBoolean(sample.processes_complete, `${at}.processes_complete`)
  });
}

function requireCount(value: unknown, path: string, capacity: number): number {
  const count = requireNonNegativeSafeInteger(value, path);
  if (count > capacity) throw new ResponseDecodeError(path, `at most ${capacity}`);
  return count;
}

function decodeDisk(value: unknown, path: string): FreezeRecordDisk {
  const record = requireRecord(value, path);
  return Object.freeze({
    physicalDiskId: requireNonNegativeSafeInteger(record.physical_disk_id, `${path}.physical_disk_id`),
    queueDepth: requireNullable(record.queue_depth, `${path}.queue_depth`, requireNonNegativeFiniteNumber),
    latencyMs: requireNullable(record.latency_ms, `${path}.latency_ms`, requireNonNegativeFiniteNumber),
    completedOperations: requireNullable(record.completed_operations, `${path}.completed_operations`, requireNonNegativeFiniteNumber)
  });
}

function decodeProcess(value: unknown, path: string): FreezeRecordProcess {
  const record = requireRecord(value, path);
  const length = requireCount(record.image_name_len, `${path}.image_name_len`, 255);
  return Object.freeze({
    pid: requireNonNegativeSafeInteger(record.pid, `${path}.pid`),
    imageName: decodeImageName(record.image_name, length, `${path}.image_name`),
    privateCommitBytes: requireNonNegativeFiniteNumber(record.private_commit_bytes, `${path}.private_commit_bytes`)
  });
}

/** Image names may be UTF-8 strings or byte arrays; slice bytes by image_name_len before decoding. */
function decodeImageName(value: unknown, length: number, path: string): string {
  let bytes: Uint8Array;
  if (typeof value === "string") {
    bytes = new TextEncoder().encode(value);
  } else {
    const items = requireArray(value, path).map((item, index) => {
      const byte = requireNonNegativeSafeInteger(item, `${path}[${index}]`);
      if (byte > 255) throw new ResponseDecodeError(`${path}[${index}]`, "a byte");
      return byte;
    });
    bytes = Uint8Array.from(items);
  }
  if (bytes.length < length) throw new ResponseDecodeError(path, `at least image_name_len (${length}) bytes`);
  return new TextDecoder().decode(bytes.subarray(0, length));
}

function decodeReviewEvent(value: unknown, path: string): FreezeReviewEvent {
  const record = requireRecord(value, path);
  return Object.freeze({
    category: requireOneOf(record.category, `${path}.category`, systemEventRuleCategories),
    targetKind: requireOneOf(record.targetKind, `${path}.targetKind`, systemEventTargetKinds),
    target: requireString(record.target, `${path}.target`),
    raw: decodeSystemEventRaw(record.raw, `${path}.raw`),
    actualShutdownAt: requireNullable(record.actualShutdownAt, `${path}.actualShutdownAt`, requireSafeInteger)
  });
}

type Client = Pick<RequestClient, "request">;

/** Read immutable review details once when opened. */
export function readFreezeReviewDetails(client: Client, id: string, signal?: AbortSignal): Promise<FreezeReviewDetails> {
  return client.request({
    key: `freeze-recorder.review-details.${id}`,
    url: `/api/freeze-recorder/reviews/${encodeURIComponent(id)}/details`,
    fallbackError: uiText.freezeReports.recorder.details.readFailed,
    decoder: freezeReviewDetailsDecoder,
    signal
  });
}

const exportBytesDecoder = defineResponseDecoder<ArrayBuffer>("freeze-recorder.review-export.v1", (value) => {
  if (!(value instanceof ArrayBuffer)) throw new ResponseDecodeError("$", "binary export");
  return value;
});

/** Download the backend freeze-review.json bytes unchanged. */
export async function exportFreezeReview(client: Client, id: string): Promise<void> {
  const bytes = await client.request({
    key: `freeze-recorder.review-export.${id}`,
    url: `/api/freeze-recorder/reviews/${encodeURIComponent(id)}/export`,
    fallbackError: uiText.freezeReports.recorder.details.exportFailed,
    decoder: exportBytesDecoder,
    timeoutMs: 60_000,
    request: { binary: true }
  });
  const url = URL.createObjectURL(new Blob([bytes], { type: "application/json" }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = `freeze-review-${id}.json`;
  anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Sort by shutdown time, falling back to last sample time, newest first. */
export function freezeReviewsInDisplayOrder(items: readonly FreezeReviewSummary[]): FreezeReviewSummary[] {
  return [...items].sort((left, right) => (right.shutdownAt ?? right.lastRecordAt) - (left.shutdownAt ?? left.lastRecordAt));
}
