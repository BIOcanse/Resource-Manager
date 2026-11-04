import {
  defineResponseDecoder,
  requireArray,
  requireBackendMessage,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNonNegativeFiniteNumber,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { BackendMessage } from "../../types.ts";

/* Observation runs independently of UI subscriptions. Status describes the current observation; items retain unique process/resource identities across active and history states. Full capacity counts omitted items without eviction. Invalid shapes fail decoding. */

/** Logical selector requiring processes.read; pushes report changes without a sampling interval. */
export function buildKernelLeakReportSubscriptionUrl(): string {
  return "/api/kernel-leaks/subscribe";
}

// Require known backend report states; an unknown value invalidates the report.
export const kernelLeakReportStatuses = ["NotObserved", "Observing", "Empty", "Suspected", "Unavailable"] as const;
export const kernelLeakFailures = ["SourceUnavailable", "CapacityExceeded", "InvalidObservation"] as const;
/** Public resource kinds only; internal FileCachePoolBytes must never appear in reports. */
export const kernelLeakResourceKinds = ["ProcessHandles"] as const;
export const kernelLeakUnits = ["count"] as const;
export const kernelLeakMetadataStatuses = ["Current", "Unavailable", "ProcessEnded"] as const;
export const kernelLeakRestartPolicies = ["NextInterval"] as const;
export const kernelLeakActionKinds = ["RestartHolder", "ForceReleaseHandles"] as const;
export const kernelLeakRiskLevels = ["Warning", "High"] as const;
export const kernelLeakItemStates = ["active", "history"] as const;

export type KernelLeakFailure = typeof kernelLeakFailures[number];
export type KernelLeakResourceKind = typeof kernelLeakResourceKinds[number];
export type KernelLeakUnit = typeof kernelLeakUnits[number];
export type KernelLeakRestartPolicy = typeof kernelLeakRestartPolicies[number];
export type KernelLeakActionKind = typeof kernelLeakActionKinds[number];
export type KernelLeakRiskLevel = typeof kernelLeakRiskLevels[number];
export type KernelLeakItemState = typeof kernelLeakItemStates[number];

/** Current images require name and path; absent product metadata is null. */
export type KernelLeakImage =
  | { readonly status: "Current"; readonly name: string; readonly path: string; readonly productName: string | null }
  | { readonly status: "Unavailable" | "ProcessEnded" };

/** A Current empty list means the process hosts no matching services. */
export type KernelLeakServices =
  | { readonly status: "Current"; readonly names: readonly string[] }
  | { readonly status: "Unavailable" | "ProcessEnded" };

export interface KernelLeakHolder {
  readonly processId: number;
  /** Decimal FILETIME text combined with PID identifies the process and action target. */
  readonly startKey: string;
  readonly image: KernelLeakImage;
  readonly services: KernelLeakServices;
}

/** Actions have no execution endpoint, so all must be disabled with backend reason and risk messages. */
export interface KernelLeakAction {
  readonly action: KernelLeakActionKind;
  readonly enabled: false;
  readonly disabledReason: BackendMessage;
  readonly risk: BackendMessage;
  readonly riskLevel: KernelLeakRiskLevel;
}

export interface KernelLeakItem {
  readonly state: KernelLeakItemState;
  /** First and latest suspicion times stop changing once an item enters history. */
  readonly firstObservedAt: string;
  readonly lastObservedAt: string;
  readonly resourceKind: KernelLeakResourceKind;
  readonly unit: KernelLeakUnit;
  readonly currentValue: number;
  readonly growthPerSecond: number;
  readonly observationSeconds: number;
  readonly sampleCount: number;
  readonly growth: number;
  readonly holder: KernelLeakHolder;
  readonly actions: readonly KernelLeakAction[];
}

export interface KernelLeakRunFailure {
  /** First-frame time of the failed observation; failures require an observation that actually started. */
  readonly startedAt: string;
  readonly failedAt: string;
  readonly failure: KernelLeakFailure;
}

export interface KernelLeakObservation {
  /** First-frame time, or null before observation starts; null requires zero duration and samples. */
  readonly startedAt: string | null;
  readonly elapsedSeconds: number;
  readonly sampleCount: number;
  readonly requiredSeconds: number;
  readonly requiredSampleCount: number;
  readonly previousRunFailure: KernelLeakRunFailure | null;
  readonly restartPolicy: KernelLeakRestartPolicy;
}

interface KernelLeakReportCommon {
  readonly observation: KernelLeakObservation;
  /** Holders without readable values are excluded from this frame, without blocking other holders. */
  readonly unavailableHolderCount: number;
  readonly items: readonly KernelLeakItem[];
  /** Suspected items omitted because record capacity was reached. */
  readonly unrecordedItemCount: number;
}

export type KernelLeakReport =
  | KernelLeakReportCommon & { readonly status: "NotObserved" | "Observing"; readonly capturedAt: string | null }
  | KernelLeakReportCommon & { readonly status: "Empty"; readonly capturedAt: string }
  | KernelLeakReportCommon & { readonly status: "Suspected"; readonly capturedAt: string }
  | KernelLeakReportCommon & { readonly status: "Unavailable"; readonly capturedAt: string | null; readonly failure: KernelLeakFailure };

export const kernelLeakReportDecoder = defineResponseDecoder<KernelLeakReport>(
  "kernel-leak-report.v3",
  (value) => {
    const record = requireRecord(value);
    const status = requireOneOf(record.status, "$.status", kernelLeakReportStatuses);
    const failure = requireOneOf(record.failure, "$.failure", ["None", ...kernelLeakFailures] as const);
    const capturedAt = requireNullable(record.capturedAt, "$.capturedAt", requireTimestamp);
    const observation = decodeObservation(record.observation, "$.observation");
    const unavailableHolderCount = requireNonNegativeSafeInteger(record.unavailableHolderCount, "$.unavailableHolderCount");
    const items = decodeItems(record.items, "$.items");
    const unrecordedItemCount = requireNonNegativeSafeInteger(record.unrecordedItemCount, "$.unrecordedItemCount");
    const common = { observation, unavailableHolderCount, items, unrecordedItemCount };

    if (status === "Unavailable") {
      if (failure === "None") throw new ResponseDecodeError("$.failure", "a failure reason when status is 'Unavailable'");
      return { ...common, status, capturedAt, failure };
    }
    if (failure !== "None") throw new ResponseDecodeError("$.failure", `'None' when status is '${status}'`);
    const hasActive = items.some((item) => item.state === "active");
    if (status === "Suspected" && !hasActive) throw new ResponseDecodeError("$.items", "at least one active item when status is 'Suspected'");
    if (status === "Empty" && hasActive) throw new ResponseDecodeError("$.items", "no active item when status is 'Empty'");
    if (status === "NotObserved" || status === "Observing") return { ...common, status, capturedAt };
    if (capturedAt === null) throw new ResponseDecodeError("$.capturedAt", `a capture time when status is '${status}'`);
    return { ...common, status, capturedAt };
  });

function decodeObservation(value: unknown, path: string): KernelLeakObservation {
  const record = requireRecord(value, path);
  return {
    startedAt: requireNullable(record.startedAt, `${path}.startedAt`, requireTimestamp),
    elapsedSeconds: requireNonNegativeFiniteNumber(record.elapsedSeconds, `${path}.elapsedSeconds`),
    sampleCount: requireNonNegativeSafeInteger(record.sampleCount, `${path}.sampleCount`),
    requiredSeconds: requireNonNegativeSafeInteger(record.requiredSeconds, `${path}.requiredSeconds`),
    requiredSampleCount: requireNonNegativeSafeInteger(record.requiredSampleCount, `${path}.requiredSampleCount`),
    previousRunFailure: requireNullable(record.previousRunFailure, `${path}.previousRunFailure`, (item, itemPath) => {
      const failure = requireRecord(item, itemPath);
      return {
        startedAt: requireTimestamp(failure.startedAt, `${itemPath}.startedAt`),
        failedAt: requireTimestamp(failure.failedAt, `${itemPath}.failedAt`),
        failure: requireOneOf(failure.failure, `${itemPath}.failure`, kernelLeakFailures)
      };
    }),
    restartPolicy: requireOneOf(record.restartPolicy, `${path}.restartPolicy`, kernelLeakRestartPolicies)
  };
}

/** One resource kind for one process identity, including its start key to distinguish reused PIDs. */
export function kernelLeakItemKey(item: Pick<KernelLeakItem, "resourceKind" | "holder">): string {
  return `${item.resourceKind}:${item.holder.processId}:${item.holder.startKey}`;
}

/** Unique resource/process identities, ordered by first suspicion, oldest first. */
function decodeItems(value: unknown, path: string): readonly KernelLeakItem[] {
  const items = requireArray(value, path).map((item, index) => decodeItem(item, `${path}[${index}]`));
  const keys = new Set<string>();
  for (const [index, item] of items.entries()) {
    const key = kernelLeakItemKey(item);
    if (keys.has(key)) throw new ResponseDecodeError(`${path}[${index}]`, "one item per holder process and resource kind");
    keys.add(key);
    if (index > 0 && Date.parse(item.firstObservedAt) < Date.parse(items[index - 1]!.firstObservedAt)) {
      throw new ResponseDecodeError(`${path}[${index}].firstObservedAt`, "items in ascending firstObservedAt order");
    }
  }
  return items;
}

function decodeItem(value: unknown, path: string): KernelLeakItem {
  const record = requireRecord(value, path);
  const actions = requireArray(record.actions, `${path}.actions`).map((item, index) => decodeAction(item, `${path}.actions[${index}]`));
  const kinds = new Set<KernelLeakActionKind>();
  for (const [index, action] of actions.entries()) {
    if (kinds.has(action.action)) throw new ResponseDecodeError(`${path}.actions[${index}].action`, "each action at most once per item");
    kinds.add(action.action);
  }
  const firstObservedAt = requireTimestamp(record.firstObservedAt, `${path}.firstObservedAt`);
  const lastObservedAt = requireTimestamp(record.lastObservedAt, `${path}.lastObservedAt`);
  if (Date.parse(lastObservedAt) < Date.parse(firstObservedAt)) {
    throw new ResponseDecodeError(`${path}.lastObservedAt`, "a time not earlier than firstObservedAt");
  }
  return {
    state: requireOneOf(record.state, `${path}.state`, kernelLeakItemStates),
    firstObservedAt,
    lastObservedAt,
    resourceKind: requireOneOf(record.resourceKind, `${path}.resourceKind`, kernelLeakResourceKinds),
    unit: requireOneOf(record.unit, `${path}.unit`, kernelLeakUnits),
    currentValue: requireNonNegativeSafeInteger(record.currentValue, `${path}.currentValue`),
    growthPerSecond: requireFiniteNumber(record.growthPerSecond, `${path}.growthPerSecond`),
    observationSeconds: requireNonNegativeFiniteNumber(record.observationSeconds, `${path}.observationSeconds`),
    sampleCount: requireNonNegativeSafeInteger(record.sampleCount, `${path}.sampleCount`),
    growth: requireNonNegativeSafeInteger(record.growth, `${path}.growth`),
    holder: decodeHolder(record.holder, `${path}.holder`),
    actions
  };
}

function decodeAction(value: unknown, path: string): KernelLeakAction {
  const record = requireRecord(value, path);
  if (requireBoolean(record.enabled, `${path}.enabled`)) {
    throw new ResponseDecodeError(`${path}.enabled`, "false: contract v2 reserves the actions and the frontend has no command for them");
  }
  return {
    action: requireOneOf(record.action, `${path}.action`, kernelLeakActionKinds),
    enabled: false,
    disabledReason: requireBackendMessage(record.disabledReason, `${path}.disabledReason`),
    risk: requireBackendMessage(record.risk, `${path}.risk`),
    riskLevel: requireOneOf(record.riskLevel, `${path}.riskLevel`, kernelLeakRiskLevels)
  };
}

function decodeHolder(value: unknown, path: string): KernelLeakHolder {
  const record = requireRecord(value, path);
  const processId = requireNonNegativeSafeInteger(record.processId, `${path}.processId`);
  const startKey = requireString(record.startKey, `${path}.startKey`);
  if (!/^\d+$/.test(startKey)) throw new ResponseDecodeError(`${path}.startKey`, "decimal FILETIME text");
  return { processId, startKey, image: decodeImage(record.image, `${path}.image`), services: decodeServices(record.services, `${path}.services`) };
}

function decodeImage(value: unknown, path: string): KernelLeakImage {
  const record = requireRecord(value, path);
  const status = requireOneOf(record.status, `${path}.status`, kernelLeakMetadataStatuses);
  if (status !== "Current") {
    for (const key of ["name", "path", "productName"]) {
      if (key in record) throw new ResponseDecodeError(`${path}.${key}`, `no value when status is '${status}'`);
    }
    return { status };
  }
  return {
    status,
    name: requireNonEmptyString(record.name, `${path}.name`),
    path: requireNonEmptyString(record.path, `${path}.path`),
    // The backend omits this key when product metadata is absent.
    productName: "productName" in record ? requireNonEmptyString(record.productName, `${path}.productName`) : null
  };
}

function decodeServices(value: unknown, path: string): KernelLeakServices {
  const record = requireRecord(value, path);
  const status = requireOneOf(record.status, `${path}.status`, kernelLeakMetadataStatuses);
  const names = requireArray(record.names, `${path}.names`).map((item, index) => requireNonEmptyString(item, `${path}.names[${index}]`));
  if (status === "Current") return { status, names };
  if (names.length > 0) throw new ResponseDecodeError(`${path}.names`, `an empty list when status is '${status}'`);
  return { status };
}

function requireTimestamp(value: unknown, path: string): string {
  const timestamp = requireNonEmptyString(value, path);
  if (!/(?:Z|[+-]\d{2}:\d{2})$/.test(timestamp) || !Number.isFinite(Date.parse(timestamp))) {
    throw new ResponseDecodeError(path, "ISO timestamp with an explicit offset");
  }
  return timestamp;
}
