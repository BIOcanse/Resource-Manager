import {
  defineResponseDecoder, requireArray, requireBoolean, requireFiniteNumber, requireNonEmptyString,
  requireNonNegativeFiniteNumber, requireNonNegativeSafeInteger, requireNullable, requireOneOf, requireRecord, requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

/* The backend normalizes sustained high-priority CPU monopolizers and owns settings, state and records. Unknown reason codes remain displayable. */

/** Backend-owned settings and ranges; the UI keeps no defaults. */
export interface CpuMonopolyProtectionSettings {
  enabled: boolean;
  thresholdPercent: number;
  sustainedSeconds: number;
  checkIntervalSeconds: number;
  recordCapacity: number;
  processCapacity: number;
  subscriberCapacity: number;
  maximumSampleGapSeconds: number;
}

/** Backend-validated ranges shared by the input controls. */
export const cpuMonopolySettingRanges = {
  thresholdPercent: { minimum: 0.1, maximum: 100 },
  seconds: { minimum: 1, maximum: 86400 }
} as const;

export interface CpuMonopolyExclusion {
  readonly processId: number;
  readonly processStartKey: string;
  readonly name: string;
  readonly reason: string;
}

export interface CpuMonopolyActionRecord {
  readonly actionId: string;
  readonly processId: number;
  readonly processStartKey: string;
  readonly name: string;
  readonly at: string;
  readonly cpuPercent: number;
  readonly previousPriorityClass: string;
  readonly thresholdPercent: number;
  readonly sustainedSeconds: number;
  /** Active records have normalized live processes; exited processes and failed actions are history. */
  readonly state: "active" | "history";
  readonly outcome: "normalized" | "failed";
  readonly reason: string | null;
  readonly detail: string | null;
  readonly endedAt: string | null;
  readonly endReason: string | null;
}

/** Live status; records arrive in separate frames. */
export interface CpuMonopolyLiveStatus {
  readonly enabled: boolean;
  readonly active: boolean;
  readonly inactiveReason: string | null;
  readonly thresholdPercent: number;
  readonly sustainedSeconds: number;
  readonly checkIntervalSeconds: number;
  readonly currentActionId: string | null;
  readonly excludedCandidates: readonly CpuMonopolyExclusion[];
}

export interface CpuMonopolyEvent {
  readonly kind: string;
  readonly reason: string | null;
  readonly record: CpuMonopolyActionRecord | null;
}

export type CpuMonopolyFrame =
  | { readonly type: "status"; readonly status: CpuMonopolyLiveStatus }
  | { readonly type: "records"; readonly records: readonly CpuMonopolyActionRecord[]; readonly limit: number;
    readonly unrecordedRecordCount: number }
  | { readonly type: "record"; readonly record: CpuMonopolyActionRecord }
  | { readonly type: "event"; readonly event: CpuMonopolyEvent };

/** Folded status, newest-first records, capacity and count omitted at capacity. */
export interface CpuMonopolyView {
  readonly status: CpuMonopolyLiveStatus;
  readonly records: readonly CpuMonopolyActionRecord[];
  readonly recordCapacity: number;
  readonly unrecordedRecordCount: number;
}

/** Known codes provide labels without restricting decoding. */
export const cpuMonopolyInactiveReasons = ["disabled", "not-running", "process-data-unavailable",
  "process-exit-proof-unavailable", "process-capacity", "stopped"] as const;
export const cpuMonopolyExclusionReasons = ["resource-manager", "protected-process", "critical-process",
  "resource-manager-process", "unsupported-platform", "identity-changed", "cannot-open", "criticality-unavailable"] as const;
export const cpuMonopolyActionReasons = ["process-exited", "user-priority-set", "write-failed", "identity-changed",
  "priority-changed", "priority-unavailable", "priority-write-failed", "priority-readback-unavailable",
  "priority-readback-mismatch"] as const;
export const cpuMonopolyFrameTypes = ["status", "records", "record", "event"] as const;

function timestamp(value: unknown, path: string): string {
  const text = requireString(value, path);
  if (!Number.isFinite(Date.parse(text))) throw new ResponseDecodeError(path, "timestamp");
  return text;
}

/** Treat omitted and explicit null values as absent. */
function optional<T>(value: unknown, path: string, decode: (value: unknown, path: string) => T): T | null {
  return value === undefined ? null : requireNullable(value, path, decode);
}

const rows = <T>(value: unknown, path: string, decode: (value: unknown, path: string) => T): T[] =>
  requireArray(value, path).map((row, index) => decode(row, `${path}[${index}]`));

export function decodeCpuMonopolyProtectionSettings(value: unknown, path = "$"): CpuMonopolyProtectionSettings {
  const row = requireRecord(value, path);
  const positiveInteger = (key: string) => {
    const number = requireNonNegativeSafeInteger(row[key], `${path}.${key}`);
    if (number < 1) throw new ResponseDecodeError(`${path}.${key}`, "positive integer");
    return number;
  };
  return {
    enabled: requireBoolean(row.enabled, `${path}.enabled`),
    thresholdPercent: requireNonNegativeFiniteNumber(row.thresholdPercent, `${path}.thresholdPercent`),
    sustainedSeconds: requireNonNegativeFiniteNumber(row.sustainedSeconds, `${path}.sustainedSeconds`),
    checkIntervalSeconds: requireNonNegativeFiniteNumber(row.checkIntervalSeconds, `${path}.checkIntervalSeconds`),
    recordCapacity: positiveInteger("recordCapacity"),
    processCapacity: positiveInteger("processCapacity"),
    subscriberCapacity: positiveInteger("subscriberCapacity"),
    maximumSampleGapSeconds: requireNonNegativeFiniteNumber(row.maximumSampleGapSeconds, `${path}.maximumSampleGapSeconds`)
  };
}

export function decodeCpuMonopolyExclusion(value: unknown, path = "$"): CpuMonopolyExclusion {
  const row = requireRecord(value, path);
  return Object.freeze({
    processId: requireNonNegativeSafeInteger(row.processId, `${path}.processId`),
    processStartKey: requireString(row.processStartKey, `${path}.processStartKey`),
    name: requireString(row.name, `${path}.name`),
    reason: requireNonEmptyString(row.reason, `${path}.reason`)
  });
}

export function decodeCpuMonopolyActionRecord(value: unknown, path = "$"): CpuMonopolyActionRecord {
  const row = requireRecord(value, path);
  const state = requireOneOf(row.state, `${path}.state`, ["active", "history"] as const);
  const outcome = requireOneOf(row.outcome, `${path}.outcome`, ["normalized", "failed"] as const);
  // Failed actions enter history; only normalized processes can remain active.
  if (state === "active" && outcome === "failed") throw new ResponseDecodeError(`${path}.state`, "history for failed action");
  return Object.freeze({
    actionId: requireNonEmptyString(row.actionId, `${path}.actionId`),
    processId: requireNonNegativeSafeInteger(row.processId, `${path}.processId`),
    processStartKey: requireString(row.processStartKey, `${path}.processStartKey`),
    name: requireString(row.name, `${path}.name`),
    at: timestamp(row.at, `${path}.at`),
    cpuPercent: requireFiniteNumber(row.cpuPercent, `${path}.cpuPercent`),
    previousPriorityClass: requireNonEmptyString(row.previousPriorityClass, `${path}.previousPriorityClass`),
    thresholdPercent: requireFiniteNumber(row.thresholdPercent, `${path}.thresholdPercent`),
    sustainedSeconds: requireFiniteNumber(row.sustainedSeconds, `${path}.sustainedSeconds`),
    state, outcome,
    reason: optional(row.reason, `${path}.reason`, requireString),
    detail: optional(row.detail, `${path}.detail`, requireString),
    endedAt: optional(row.endedAt, `${path}.endedAt`, timestamp),
    endReason: optional(row.endReason, `${path}.endReason`, requireString)
  });
}

export function decodeCpuMonopolyLiveStatus(value: unknown, path = "$"): CpuMonopolyLiveStatus {
  const row = requireRecord(value, path);
  const enabled = requireBoolean(row.enabled, `${path}.enabled`);
  const active = requireBoolean(row.active, `${path}.active`);
  const inactiveReason = optional(row.inactiveReason, `${path}.inactiveReason`, requireNonEmptyString);
  return Object.freeze({
    enabled, active, inactiveReason,
    thresholdPercent: requireFiniteNumber(row.thresholdPercent, `${path}.thresholdPercent`),
    sustainedSeconds: requireFiniteNumber(row.sustainedSeconds, `${path}.sustainedSeconds`),
    checkIntervalSeconds: requireFiniteNumber(row.checkIntervalSeconds, `${path}.checkIntervalSeconds`),
    currentActionId: optional(row.currentActionId, `${path}.currentActionId`, requireNonEmptyString),
    excludedCandidates: Object.freeze(rows(row.excludedCandidates, `${path}.excludedCandidates`, decodeCpuMonopolyExclusion))
  });
}

export function decodeCpuMonopolyFrame(value: unknown, path = "$"): CpuMonopolyFrame {
  const row = requireRecord(value, path);
  const type = requireOneOf(row.type, `${path}.type`, cpuMonopolyFrameTypes);
  switch (type) {
    case "status":
      return { type, status: decodeCpuMonopolyLiveStatus(row.status, `${path}.status`) };
    case "records":
      return { type, records: Object.freeze(rows(row.records, `${path}.records`, decodeCpuMonopolyActionRecord)),
        limit: requireNonNegativeSafeInteger(row.limit, `${path}.limit`),
        unrecordedRecordCount: requireNonNegativeSafeInteger(row.unrecordedRecordCount, `${path}.unrecordedRecordCount`) };
    case "record":
      return { type, record: decodeCpuMonopolyActionRecord(row.record, `${path}.record`) };
    case "event": {
      const event = requireRecord(row.event, `${path}.event`);
      return { type, event: Object.freeze({
        kind: requireNonEmptyString(event.kind, `${path}.event.kind`),
        reason: optional(event.reason, `${path}.event.reason`, requireString),
        record: optional(event.record, `${path}.event.record`, decodeCpuMonopolyActionRecord)
      }) };
    }
  }
}

export const cpuMonopolyFrameDecoder = defineResponseDecoder("cpu-monopoly-protection.frame.v1", decodeCpuMonopolyFrame);

export function buildCpuMonopolySubscriptionUrl(): string {
  return "/api/cpu-monopoly-protection/subscribe";
}
