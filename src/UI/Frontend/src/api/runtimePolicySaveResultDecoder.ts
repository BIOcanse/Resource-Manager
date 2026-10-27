import type { RuntimePolicySaveResult } from "../types.ts";
import {
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord
} from "../frontendRuntime/request/ResponseDecoder.ts";

/** Policy publication acknowledges delivery, not process binding or scheduler execution. */
export function decodeRuntimePolicySaveResult<T>(value: unknown): RuntimePolicySaveResult<T> {
  const record = requireRecord(value);
  return {
    policy: requireRecord(record.policy, "$.policy") as T,
    startupInterception: record.startupInterception == null
      ? record.startupInterception
      : requireRecord(record.startupInterception, "$.startupInterception") as unknown as RuntimePolicySaveResult<T>["startupInterception"],
    runtimeApplicationDisposition: requireOneOf(record.runtimeApplicationDisposition,
      "$.runtimeApplicationDisposition", ["committedAndPublished", "committedWithDeliveryFailures", "savedNotApplied"]),
    startupInterceptionDisposition: requireOneOf(record.startupInterceptionDisposition,
      "$.startupInterceptionDisposition", ["applied", "notAttempted", "applyFailed"]),
    runtimePlanVersion: requireNullable(record.runtimePlanVersion, "$.runtimePlanVersion", requireNonNegativeSafeInteger),
    runtimePublicationSequence: requireNullable(record.runtimePublicationSequence, "$.runtimePublicationSequence", requireNonNegativeSafeInteger),
    runtimeDeliveryFailureCount: requireNonNegativeSafeInteger(record.runtimeDeliveryFailureCount, "$.runtimeDeliveryFailureCount"),
    failureCode: requireNullable(record.failureCode, "$.failureCode", requireNonEmptyString)
  };
}
