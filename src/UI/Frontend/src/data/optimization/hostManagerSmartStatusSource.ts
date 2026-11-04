import type {
  AppOptimizationMode,
  HostManagerCycleFailure,
  HostManagerFactExclusion,
  HostManagerFactExclusionCount,
  HostManagerSmartCoordinatorStatus
} from "../../types.ts";
import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireSafeInteger,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

import { decodeCoordinatorFacts } from "../../api/hostManagerRollbackWire.ts";
import {
  hostManagerNativePlanStatuses,
  hostManagerSchedulingExclusionReasons,
  hostManagerSchedulingFacts
} from "./hostManagerSchedulingFactCodes.ts";

const optimizationModes = [
  "normal", "limited", "cpu", "gpu",
  "memory+cpu", "memory+gpu", "cpu+gpu", "smart"
] as const satisfies readonly AppOptimizationMode[];

/** Backend maximum number of scheduling exclusions. */
const maximumFactExclusions = 64;

/** Backend-coordinated scheduling status, decoded for direct display. */
export function decodeHostManagerSmartCoordinatorStatus(
  value: unknown
): HostManagerSmartCoordinatorStatus {
  const record = requireRecord(value);
  return {
    mode: requireOneOf(record.mode, "$.mode", optimizationModes),
    schedulerRunning: requireBoolean(record.schedulerRunning, "$.schedulerRunning"),
    lastRunAt: requireNullable(record.lastRunAt, "$.lastRunAt", requireString),
    lastRestoreAt: requireNullable(record.lastRestoreAt, "$.lastRestoreAt", requireString),
    pendingChangeCount: requireNonNegativeSafeInteger(
      record.pendingChangeCount,
      "$.pendingChangeCount"),
    appliedTargetCount: requireNonNegativeSafeInteger(
      record.appliedTargetCount,
      "$.appliedTargetCount"),
    faultClosedReason: requireNullable(
      record.faultClosedReason,
      "$.faultClosedReason",
      requireString),
    coordinator: decodeCoordinatorFacts(record.coordinator),
    failure: requireNullable(record.failure, "$.failure", decodeCycleFailure),
    factExclusionCount: requireNonNegativeSafeInteger(
      record.factExclusionCount,
      "$.factExclusionCount"),
    factExclusionCounts: requireArray(record.factExclusionCounts, "$.factExclusionCounts")
      .map((item, index) => decodeFactExclusionCount(item, `$.factExclusionCounts[${index}]`)),
    factExclusions: decodeFactExclusions(record.factExclusions, "$.factExclusions")
  };
}

/** Only native-plan failures carry a plan status code. */
function decodeCycleFailure(value: unknown, path: string): HostManagerCycleFailure {
  const record = requireRecord(value, path);
  const kind = requireOneOf(record.kind, `${path}.kind`, ["cycle-failed", "native-plan-failed"] as const);
  const exceptionType = requireNonEmptyString(record.exceptionType, `${path}.exceptionType`);
  if (kind === "cycle-failed") {
    if (record.nativePlanStatus !== null) {
      throw new ResponseDecodeError(`${path}.nativePlanStatus`, "null");
    }
    return { kind, exceptionType, nativePlanStatus: null };
  }
  return {
    kind,
    exceptionType,
    nativePlanStatus: requireOneOf(
      record.nativePlanStatus,
      `${path}.nativePlanStatus`,
      hostManagerNativePlanStatuses)
  };
}

function decodeFactExclusionCount(value: unknown, path: string): HostManagerFactExclusionCount {
  const record = requireRecord(value, path);
  return {
    fact: requireOneOf(record.fact, `${path}.fact`, hostManagerSchedulingFacts),
    reason: requireOneOf(record.reason, `${path}.reason`, hostManagerSchedulingExclusionReasons),
    count: requireNonNegativeSafeInteger(record.count, `${path}.count`)
  };
}

function decodeFactExclusions(value: unknown, path: string): HostManagerFactExclusion[] {
  const items = requireArray(value, path);
  if (items.length > maximumFactExclusions) {
    throw new ResponseDecodeError(path, `at most ${maximumFactExclusions} items`);
  }
  return items.map((item, index) => {
    const itemPath = `${path}[${index}]`;
    const record = requireRecord(item, itemPath);
    return {
      processId: requireNullable(record.processId, `${itemPath}.processId`, requireSafeInteger),
      processStartKey: requireNullable(
        record.processStartKey,
        `${itemPath}.processStartKey`,
        requireUnsignedIntegerText),
      targetId: requireNullable(record.targetId, `${itemPath}.targetId`, requireString),
      fact: requireOneOf(record.fact, `${itemPath}.fact`, hostManagerSchedulingFacts),
      reason: requireOneOf(record.reason, `${itemPath}.reason`, hostManagerSchedulingExclusionReasons),
      adapterKey: requireNullable(record.adapterKey, `${itemPath}.adapterKey`, requireUnsignedIntegerText)
    };
  });
}

/** Preserve ulong process start keys and GPU adapter IDs as decimal text, without Number conversion. */
function requireUnsignedIntegerText(value: unknown, path: string): string {
  const text = requireString(value, path);
  if (!/^(0|[1-9][0-9]*)$/.test(text) || BigInt(text) > 18446744073709551615n) {
    throw new ResponseDecodeError(path, "unsigned 64-bit integer text");
  }
  return text;
}

export const hostManagerSmartStatusDecoder = defineResponseDecoder(
  "host-manager.smart-status.v1",
  decodeHostManagerSmartCoordinatorStatus);

export function buildHostManagerSmartStatusSubscriptionUrl(
  intervalMs: number
): string {
  const query = new URLSearchParams({
    intervalMs: String(Math.max(1, Math.ceil(intervalMs)))
  });
  return `/api/optimization/smart/status/subscribe?${query.toString()}`;
}
