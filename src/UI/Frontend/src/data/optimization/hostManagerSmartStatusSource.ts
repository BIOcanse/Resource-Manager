import type {
  AppOptimizationMode,
  HostManagerSmartCoordinatorStatus
} from "../../types.ts";
import {
  defineResponseDecoder,
  requireBoolean,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireString
} from "../../frontendRuntime/request/ResponseDecoder.ts";

import { decodeCoordinatorFacts } from "../../api/hostManagerRollbackWire.ts";

const optimizationModes = [
  "normal", "limited", "cpu", "gpu",
  "memory+cpu", "memory+gpu", "cpu+gpu", "smart"
] as const satisfies readonly AppOptimizationMode[];

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
    coordinator: decodeCoordinatorFacts(record.coordinator)
  };
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
