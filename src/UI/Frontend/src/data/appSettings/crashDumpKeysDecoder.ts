import type { AppCrashDumpKeySettings, AppDeviceRestartSettings, CrashDumpKeyDiagnostics } from "../../types.ts";
import {
  requireBoolean, requireNonNegativeSafeInteger, requireNullable, requireOneOf, requireRecord, requireString, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

/* Only enabled is UI-editable. Null keys use system defaults; previous registry values remain backend-owned. */

export const crashDumpKeyStates = ["takesEffectAfterRestart", "off", "restorePending", "configurationMismatch", "failed"] as const;
export const crashDumpKeyCombinations = ["rightCtrlScrollLockTwice", "custom"] as const;
/** Backend reason codes; display unknown codes verbatim. */
export const crashDumpKeyReasons = [
  "invalid-crash-dump-keys", "crash-dump-keys-read-failed", "crash-dump-keys-write-failed", "crash-dump-keys-verify-failed",
  "crash-dump-keys-restore-pending", "invalid-crash-dump-key-snapshot", "crash-dump-keys-access-denied",
  "crash-dump-key-value-too-large", "crash-dump-keys-configuration-mismatch", "crash-dump-keys-bridge-failed"
] as const;

const optionalUInt = (value: unknown, path: string): number | null =>
  value === undefined ? null : requireNullable(value, path, requireNonNegativeSafeInteger);

/** Actual registry state for display without inference. */
export function decodeCrashDumpKeyDiagnostics(value: unknown, path = "$.crashDumpKeys"): CrashDumpKeyDiagnostics {
  const row = requireRecord(value, path);
  return Object.freeze({
    state: requireOneOf(row.state, `${path}.state`, crashDumpKeyStates),
    keyCombination: requireOneOf(row.keyCombination, `${path}.keyCombination`, crashDumpKeyCombinations),
    dump1Keys: optionalUInt(row.dump1Keys, `${path}.dump1Keys`),
    dump2Key: optionalUInt(row.dump2Key, `${path}.dump2Key`),
    takesEffectAfterRestart: requireBoolean(row.takesEffectAfterRestart, `${path}.takesEffectAfterRestart`),
    kernelDumpConfigured: row.kernelDumpConfigured === undefined ? null
      : requireNullable(row.kernelDumpConfigured, `${path}.kernelDumpConfigured`, requireBoolean),
    reason: row.reason === undefined ? null : requireNullable(row.reason, `${path}.reason`, requireString),
    detail: row.detail === undefined ? 0 : requireNonNegativeSafeInteger(row.detail, `${path}.detail`),
    failedSlot: row.failedSlot === undefined ? 0 : requireNonNegativeSafeInteger(row.failedSlot, `${path}.failedSlot`),
    rollbackDetail: row.rollbackDetail === undefined ? 0 : requireNonNegativeSafeInteger(row.rollbackDetail, `${path}.rollbackDetail`)
  });
}

/** Validate UI fields and round-trip backend-owned previousValues unchanged. */
export function decodeCrashDumpKeySettings(value: unknown, path = "$.settings.systemIntegration.crashDumpKeys"): AppCrashDumpKeySettings {
  const row = requireRecord(value, path);
  requireBoolean(row.enabled, `${path}.enabled`);
  optionalUInt(row.dump1Keys, `${path}.dump1Keys`);
  optionalUInt(row.dump2Key, `${path}.dump2Key`);
  if (row.previousValues !== undefined && row.previousValues !== null) requireRecord(row.previousValues, `${path}.previousValues`);
  return row as unknown as AppCrashDumpKeySettings;
}

/** Device-restart history capacity: 1..16384. */
export function decodeDeviceRestartSettings(value: unknown, path = "$.settings.systemIntegration.deviceRestart"): AppDeviceRestartSettings {
  const row = requireRecord(value, path);
  const capacity = requireNonNegativeSafeInteger(row.recordCapacity, `${path}.recordCapacity`);
  if (capacity < 1) throw new ResponseDecodeError(`${path}.recordCapacity`, "positive integer");
  return row as unknown as AppDeviceRestartSettings;
}
