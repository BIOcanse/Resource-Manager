import {
  defineResponseDecoder,
  requireArray,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

/* GET and subscriptions carry complete reports grouped by category and target. Times are UTC Unix milliseconds. Source failures and LiveKernelReports scan failures are independent; unknown enums or missing fields fail decoding. */

/** Logical subscription selector requiring processes.read; pushes on report changes. */
export function buildSystemEventsSubscriptionUrl(): string {
  return "/api/system-events/subscribe";
}

// Match backend categories; unexpected_shutdown appears only in rules and review evidence.
export const systemEventCategories = ["storage_retry_reset", "gpu_reset", "memory_exhausted", "desktop_crash", "hardware_error"] as const;
export type SystemEventCategory = typeof systemEventCategories[number];
export const systemEventRuleCategories = [...systemEventCategories, "unexpected_shutdown"] as const;
export type SystemEventRuleCategory = typeof systemEventRuleCategories[number];
export const systemEventTargetKinds = ["machine", "physical_disk", "storage_port", "volume", "gpu_device", "desktop_process", "hardware"] as const;
export type SystemEventTargetKind = typeof systemEventTargetKinds[number];
export const systemEventChannels = ["system", "application"] as const;
export type SystemEventChannel = typeof systemEventChannels[number];
export const systemEventStatuses = ["observing", "not-observed", "unavailable"] as const;
export type SystemEventStatus = typeof systemEventStatuses[number];
export const systemEventEntryStates = ["active", "history"] as const;
export type SystemEventEntryState = typeof systemEventEntryStates[number];

/** Preserve raw event XML, bugcheck codes and dump paths. */
export interface SystemEventRaw {
  readonly channel: SystemEventChannel;
  readonly provider: string;
  readonly eventId: number;
  readonly recordId: number;
  readonly at: number;
  readonly xml: string;
  readonly bugcheckCode: number | null;
  readonly dumpPath: string | null;
}

export interface SystemEventEntry {
  readonly id: string;
  readonly category: SystemEventCategory;
  readonly target: string;
  readonly state: SystemEventEntryState;
  readonly count: number;
  readonly firstObservedAt: number;
  readonly lastObservedAt: number;
  readonly detailsRevision: number;
  readonly events: readonly SystemEventRaw[];
}

export interface SystemEventsReport {
  readonly status: SystemEventStatus;
  readonly failure: string | null;
  readonly liveKernelFailure: string | null;
  readonly unrecordedItemCount: number;
  readonly items: readonly SystemEventEntry[];
}

export const systemEventsReportDecoder = defineResponseDecoder<SystemEventsReport>("system-events.report.v1", (value) => {
  const record = requireRecord(value);
  requireSchema(record, "$", "system-events");
  const status = requireOneOf(record.status, "$.status", systemEventStatuses);
  const failure = requireNullable(record.failure, "$.failure", requireNonEmptyString);
  // Unavailable always identifies a failed source and includes its failure name.
  if ((status === "unavailable") !== (failure !== null)) throw new ResponseDecodeError("$.failure", "a failure exactly when status is 'unavailable'");
  const ids = new Set<string>();
  const items = requireArray(record.items, "$.items").map((item, index) => {
    const entry = decodeSystemEventEntry(item, `$.items[${index}]`);
    if (ids.has(entry.id)) throw new ResponseDecodeError(`$.items[${index}].id`, "a unique entry id");
    ids.add(entry.id);
    return entry;
  });
  return Object.freeze({
    status,
    failure,
    liveKernelFailure: requireNullable(record.liveKernelFailure, "$.liveKernelFailure", requireNonEmptyString),
    unrecordedItemCount: requireNonNegativeSafeInteger(record.unrecordedItemCount, "$.unrecordedItemCount"),
    items: Object.freeze(items)
  });
});

function decodeSystemEventEntry(value: unknown, path: string): SystemEventEntry {
  const record = requireRecord(value, path);
  if (record.schemaVersion !== 1) throw new ResponseDecodeError(`${path}.schemaVersion`, "1");
  const id = requireString(record.id, `${path}.id`);
  if (!/^[0-9a-f]{64}$/.test(id)) throw new ResponseDecodeError(`${path}.id`, "64 lowercase hexadecimal characters");
  const firstObservedAt = requireNonNegativeSafeInteger(record.firstObservedAt, `${path}.firstObservedAt`);
  const lastObservedAt = requireNonNegativeSafeInteger(record.lastObservedAt, `${path}.lastObservedAt`);
  if (lastObservedAt < firstObservedAt) throw new ResponseDecodeError(`${path}.lastObservedAt`, "a time not before firstObservedAt");
  const count = requireNonNegativeSafeInteger(record.count, `${path}.count`);
  if (count === 0) throw new ResponseDecodeError(`${path}.count`, "a positive count");
  return Object.freeze({
    id,
    category: requireOneOf(record.category, `${path}.category`, systemEventCategories),
    target: requireString(record.target, `${path}.target`),
    state: requireOneOf(record.state, `${path}.state`, systemEventEntryStates),
    count,
    firstObservedAt,
    lastObservedAt,
    detailsRevision: requireNonNegativeSafeInteger(record.detailsRevision, `${path}.detailsRevision`),
    events: Object.freeze(requireArray(record.events, `${path}.events`).map((item, index) => decodeSystemEventRaw(item, `${path}.events[${index}]`)))
  });
}

export function decodeSystemEventRaw(value: unknown, path: string): SystemEventRaw {
  const record = requireRecord(value, path);
  return Object.freeze({
    channel: requireOneOf(record.channel, `${path}.channel`, systemEventChannels),
    provider: requireNonEmptyString(record.provider, `${path}.provider`),
    eventId: requireNonNegativeSafeInteger(record.eventId, `${path}.eventId`),
    recordId: requireNonNegativeSafeInteger(record.recordId, `${path}.recordId`),
    at: requireNonNegativeSafeInteger(record.at, `${path}.at`),
    xml: requireString(record.xml, `${path}.xml`),
    bugcheckCode: requireNullable(record.bugcheckCode, `${path}.bugcheckCode`, requireNonNegativeSafeInteger),
    dumpPath: requireNullable(record.dumpPath, `${path}.dumpPath`, requireNonEmptyString)
  });
}

export function requireSchema(record: Record<string, unknown>, path: string, type: string) {
  if (record.schemaVersion !== 1) throw new ResponseDecodeError(`${path}.schemaVersion`, "1");
  if (record.type !== type) throw new ResponseDecodeError(`${path}.type`, `'${type}'`);
}

/** Sort active entries first, then by latest occurrence, newest first. */
export function systemEventEntriesInDisplayOrder(items: readonly SystemEventEntry[]): SystemEventEntry[] {
  return [...items].sort((left, right) =>
    (left.state === right.state ? 0 : left.state === "active" ? -1 : 1) || right.lastObservedAt - left.lastObservedAt);
}
