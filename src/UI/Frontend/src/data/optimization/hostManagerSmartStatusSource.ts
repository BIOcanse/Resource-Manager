import type {
  AppOptimizationMode,
  HostManagerCycleFailure,
  HostManagerFactExclusion,
  HostManagerFactExclusionCount,
  HostManagerNativePlanStatus,
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

const optimizationModes = [
  "normal", "limited", "cpu", "gpu",
  "memory+cpu", "memory+gpu", "cpu+gpu", "smart"
] as const satisfies readonly AppOptimizationMode[];

/** 后端 NativeSmartCoordinatorStatus 除 Ok 外的成员，短横线写法（NativePlanStatusCode）。 */
export const hostManagerNativePlanStatuses = [
  "invalid-argument", "abi-mismatch", "unavailable", "no-data", "buffer-too-small",
  "stale-generation", "out-of-memory", "capacity-exceeded", "conflicting-facts",
  "feedback-mismatch", "recreate-required"
] as const satisfies readonly HostManagerNativePlanStatus[];

/** 后端排除明细的上限（HostManagerSmartCoordinator.DecisionDiagnostics 的 Take(64)）。 */
const maximumFactExclusions = 64;

/** 优化页的调度状态：由后端协调器一次合成，前端解码后直接显示。 */
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

/** 失败段只有两种；原生计划失败一定带计划状态码，另一种一定不带（HostManagerCycleFailureDiagnostics）。 */
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
    fact: requireNonEmptyString(record.fact, `${path}.fact`),
    reason: requireNonEmptyString(record.reason, `${path}.reason`),
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
      fact: requireNonEmptyString(record.fact, `${itemPath}.fact`),
      reason: requireNonEmptyString(record.reason, `${itemPath}.reason`)
    };
  });
}

/** 进程启动标识是后端 ulong 的十进制文本，原样保留，不转成 number。 */
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
