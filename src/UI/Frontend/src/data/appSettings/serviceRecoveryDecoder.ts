import type { ServiceRecoveryDiagnostics } from "../../types.ts";
import {
  requireBoolean, requireNonNegativeSafeInteger, requireNullable, requireOneOf, requireRecord,
  requireString, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

export function decodeServiceRecovery(value: unknown): ServiceRecoveryDiagnostics {
  const path = "$.serviceRecovery";
  const record = requireRecord(value, path);
  return Object.freeze({
    state: requireOneOf(record.state, path + ".state", ["notApplied", "applied", "skipped", "failed", "mismatch"] as const),
    configuredResetPeriodSeconds: requireNonNegativeSafeInteger(record.configuredResetPeriodSeconds, path + ".configuredResetPeriodSeconds"),
    actual: requireNullable(record.actual, path + ".actual", (value, path) => {
      const actual = requireRecord(value, path);
      if (!Array.isArray(actual.actions)) throw new ResponseDecodeError(path + ".actions", "Expected an array");
      return Object.freeze({
        resetPeriodSeconds: requireNonNegativeSafeInteger(actual.resetPeriodSeconds, path + ".resetPeriodSeconds"),
        failureActionsOnNonCrashFailures: requireBoolean(actual.failureActionsOnNonCrashFailures, path + ".failureActionsOnNonCrashFailures"),
        actions: Object.freeze(actual.actions.map((value, i) => {
          const action = requireRecord(value, path + ".actions[" + i + "]");
          return Object.freeze({
            type: requireNonNegativeSafeInteger(action.type, path + ".actions[" + i + "].type"),
            delayMilliseconds: requireNonNegativeSafeInteger(action.delayMilliseconds, path + ".actions[" + i + "].delayMilliseconds")
          });
        }))
      });
    }),
    skipReason: requireNullable(record.skipReason, path + ".skipReason", requireString),
    writeError: requireNullable(record.writeError, path + ".writeError", requireString),
    readError: requireNullable(record.readError, path + ".readError", requireString)
  });
}
