import {
  defineResponseDecoder, requireArray, requireBoolean, requireFiniteNumber,
  requireNonNegativeFiniteNumber, requireNonNegativeSafeInteger, requireNullable,
  requireOneOf, requireRecord, requireString, requireStringArray, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type {
  HostMemoryActionRecord, HostMemoryCommit, HostMemoryExclusion, HostMemoryProcess,
  HostMemoryProtectionSettings, HostMemoryProtectionStatus, HostMemoryReserveEntryStatus,
  HostMemoryReserveStatus, NormalRunningPoint, NormalRunningPointSummary
} from "./memoryProtectionTypes.ts";

export const memoryProtectionActionModes = ["dumpThenTerminate", "terminateOnly"] as const;
// excluded/reserve-unavailable are executor control results, never status action records.
export const memoryProtectionActionOutcomes = ["preflight-failed", "suspend-failed", "slice-incomplete",
  "completed-with-release-limit", "completed", "terminated-without-dump", "termination-failed"] as const;
export const memoryProtectionExclusionReasons = ["resource-manager-process", "critical-process",
  "process-identity-unavailable", "private-commit-unavailable", "baseline-private-commit-unavailable",
  "cannot-open", "identity-changed", "criticality-unavailable"] as const;
export const memoryProtectionDirectActionReasons = ["terminate-only", "software-no-dump",
  "private-commit-exceeds-reserve", "minidump-exceeds-reserve"] as const;

function requireWireUInt64(value: unknown, path: string): number {
  const number = requireNonNegativeFiniteNumber(value, path);
  if (!Number.isInteger(number) || number > 18446744073709551615) {
    throw new ResponseDecodeError(path, "UInt64 JSON number");
  }
  return number;
}
function timestamp(value: unknown, path: string): string {
  const text = requireString(value, path);
  if (!Number.isFinite(Date.parse(text))) throw new ResponseDecodeError(path, "timestamp");
  return text;
}
const nullableReason = (value: unknown, path: string) => requireNullable(value, path, requireString);
const rows = <T>(value: unknown, path: string, decode: (value: unknown, path: string) => T): T[] =>
  requireArray(value, path).map((row, index) => decode(row, `${path}[${index}]`));

export function decodeHostMemoryProtectionSettings(value: unknown, path = "$"): HostMemoryProtectionSettings {
  const row = requireRecord(value, path);
  return {
    enabled: requireBoolean(row.enabled, `${path}.enabled`),
    thresholdPercent: requireFiniteNumber(row.thresholdPercent, `${path}.thresholdPercent`),
    checkIntervalSeconds: requireFiniteNumber(row.checkIntervalSeconds, `${path}.checkIntervalSeconds`),
    actionMode: requireOneOf(row.actionMode, `${path}.actionMode`, memoryProtectionActionModes),
    reserves: requireNullable(row.reserves, `${path}.reserves`, (value, path) => rows(value, path, (value, path) => {
      const entry = requireRecord(value, path);
      return { location: requireString(entry.location, `${path}.location`),
        sizeBytes: requireWireUInt64(entry.sizeBytes, `${path}.sizeBytes`) };
    }))
  };
}
export function decodeHostMemoryCommit(value: unknown, path = "$"): HostMemoryCommit {
  const row = requireRecord(value, path);
  return {
    currentBytes: requireWireUInt64(row.currentBytes, `${path}.currentBytes`),
    limitBytes: requireWireUInt64(row.limitBytes, `${path}.limitBytes`),
    percent: requireFiniteNumber(row.percent, `${path}.percent`)
  };
}
export function decodeHostMemoryProcess(value: unknown, path = "$"): HostMemoryProcess {
  const row = requireRecord(value, path);
  return {
    processId: requireNonNegativeSafeInteger(row.processId, `${path}.processId`),
    processStartKey: requireString(row.processStartKey, `${path}.processStartKey`),
    name: requireString(row.name, `${path}.name`),
    privateCommitBytes: requireNullable(row.privateCommitBytes, `${path}.privateCommitBytes`, requireWireUInt64),
    softwareId: nullableReason(row.softwareId, `${path}.softwareId`)
  };
}
export function decodeNormalRunningPointSummary(value: unknown, path = "$"): NormalRunningPointSummary {
  const row = requireRecord(value, path);
  return { at: timestamp(row.at, `${path}.at`), processCount: requireNonNegativeSafeInteger(row.processCount, `${path}.processCount`) };
}
export function decodeNormalRunningPoint(value: unknown, path = "$"): NormalRunningPoint {
  const row = requireRecord(value, path);
  return { ...decodeNormalRunningPointSummary(row, path),
    commit: decodeHostMemoryCommit(row.commit, `${path}.commit`),
    processes: rows(row.processes, `${path}.processes`, decodeHostMemoryProcess) };
}
export function decodeHostMemoryReserveEntryStatus(value: unknown, path = "$"): HostMemoryReserveEntryStatus {
  const row = requireRecord(value, path);
  return {
    location: requireString(row.location, `${path}.location`),
    configuredSizeBytes: requireWireUInt64(row.configuredSizeBytes, `${path}.configuredSizeBytes`),
    built: requireBoolean(row.built, `${path}.built`), reason: nullableReason(row.reason, `${path}.reason`),
    remainingBytes: requireWireUInt64(row.remainingBytes, `${path}.remainingBytes`),
    clusterBytes: requireWireUInt64(row.clusterBytes, `${path}.clusterBytes`)
  };
}
export function decodeHostMemoryReserveStatus(value: unknown, path = "$"): HostMemoryReserveStatus {
  const row = requireRecord(value, path);
  return {
    entries: rows(row.entries, `${path}.entries`, decodeHostMemoryReserveEntryStatus),
    configuredSizeBytes: requireWireUInt64(row.configuredSizeBytes, `${path}.configuredSizeBytes`),
    built: requireBoolean(row.built, `${path}.built`), reason: nullableReason(row.reason, `${path}.reason`),
    remainingBytes: requireWireUInt64(row.remainingBytes, `${path}.remainingBytes`)
  };
}
export function decodeHostMemoryExclusion(value: unknown, path = "$"): HostMemoryExclusion {
  const row = requireRecord(value, path);
  return {
    processId: requireNonNegativeSafeInteger(row.processId, `${path}.processId`),
    processStartKey: requireString(row.processStartKey, `${path}.processStartKey`),
    name: requireString(row.name, `${path}.name`),
    reason: requireOneOf(row.reason, `${path}.reason`, memoryProtectionExclusionReasons)
  };
}
export function decodeHostMemoryActionRecord(value: unknown, path = "$"): HostMemoryActionRecord {
  const row = requireRecord(value, path);
  const outcome = requireOneOf(row.outcome, `${path}.outcome`, memoryProtectionActionOutcomes);
  const reasonPath = `${path}.reason`;
  let reason: string | null;
  if (outcome === "completed") {
    if (row.reason !== null) throw new ResponseDecodeError(reasonPath, "null for completed action");
    reason = null;
  } else if (outcome === "terminated-without-dump") {
    reason = requireOneOf(row.reason, reasonPath, memoryProtectionDirectActionReasons);
  } else {
    reason = requireString(row.reason, reasonPath);
    if ((outcome === "suspend-failed" && !/^ntstatus-[0-9a-f]{8}$/.test(reason))
      || (outcome === "termination-failed" && reason !== "termination-not-observed" && !/^terminate-failed--?\d+$/.test(reason))
      || (outcome === "completed-with-release-limit" && !/^private-memory-decommit-limited chunks=\d+ bytes=\d+; /.test(reason))) {
      throw new ResponseDecodeError(reasonPath, `backend ${outcome} reason`);
    }
    // Preflight and incomplete-slice failures carry arbitrary native/IO exception messages.
  }
  return {
    at: timestamp(row.at, `${path}.at`),
    processId: requireNonNegativeSafeInteger(row.processId, `${path}.processId`),
    processStartKey: requireString(row.processStartKey, `${path}.processStartKey`), name: requireString(row.name, `${path}.name`),
    baselineBytes: requireWireUInt64(row.baselineBytes, `${path}.baselineBytes`),
    currentBytes: requireWireUInt64(row.currentBytes, `${path}.currentBytes`),
    growthBytes: requireFiniteNumber(row.growthBytes, `${path}.growthBytes`),
    dumpPaths: requireStringArray(row.dumpPaths, `${path}.dumpPaths`),
    bytesWritten: requireWireUInt64(row.bytesWritten, `${path}.bytesWritten`), outcome, reason
  };
}
export function decodeHostMemoryProtectionStatus(value: unknown, path = "$"): HostMemoryProtectionStatus {
  const row = requireRecord(value, path);
  const reserve = decodeHostMemoryReserveStatus(row.reserve, `${path}.reserve`);
  const inactiveReason = nullableReason(row.inactiveReason, `${path}.inactiveReason`);
  if (inactiveReason !== null && inactiveReason !== reserve.reason) {
    requireOneOf(inactiveReason, `${path}.inactiveReason`, ["no-normal-running-point", "disabled", "disk-reserve-not-built"]);
  }
  return {
    enabled: requireBoolean(row.enabled, `${path}.enabled`),
    thresholdPercent: requireFiniteNumber(row.thresholdPercent, `${path}.thresholdPercent`),
    checkIntervalSeconds: requireFiniteNumber(row.checkIntervalSeconds, `${path}.checkIntervalSeconds`),
    actionMode: requireOneOf(row.actionMode, `${path}.actionMode`, memoryProtectionActionModes),
    reserve,
    triggerBytes: requireNullable(row.triggerBytes, `${path}.triggerBytes`, requireFiniteNumber),
    remainingBytes: requireNullable(row.remainingBytes, `${path}.remainingBytes`, requireFiniteNumber),
    currentCommit: requireNullable(row.currentCommit, `${path}.currentCommit`, decodeHostMemoryCommit),
    normalRunningPoint: requireNullable(row.normalRunningPoint, `${path}.normalRunningPoint`, decodeNormalRunningPointSummary),
    // Service forwards reserve.reason verbatim, including OS exception text.
    inactiveReason,
    excludedCandidates: rows(row.excludedCandidates, `${path}.excludedCandidates`, decodeHostMemoryExclusion),
    actionRecordLimit: requireNonNegativeSafeInteger(row.actionRecordLimit, `${path}.actionRecordLimit`),
    actions: rows(row.actions, `${path}.actions`, decodeHostMemoryActionRecord)
  };
}
export const memoryProtectionSettingsDecoder = defineResponseDecoder("memory-protection.settings.v1", decodeHostMemoryProtectionSettings);
export const memoryProtectionStatusDecoder = defineResponseDecoder("memory-protection.status.v1", decodeHostMemoryProtectionStatus);
export const normalRunningPointDecoder = defineResponseDecoder("memory-protection.normal-running-point.v1", decodeNormalRunningPoint);
export const nullableNormalRunningPointDecoder = defineResponseDecoder<NormalRunningPoint | null>(
  "memory-protection.normal-running-point.nullable.v1", value => requireNullable(value, "$", decodeNormalRunningPoint));
