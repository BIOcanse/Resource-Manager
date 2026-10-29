import {
  defineResponseDecoder, requireArray, requireBoolean, requireFiniteNumber,
  requireNonEmptyString, requireNonNegativeFiniteNumber, requireNonNegativeSafeInteger, requireNullable,
  requireOneOf, requireRecord, requireString, requireStringArray, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type {
  HostMemoryActionOutcome, HostMemoryActionReason, HostMemoryActionRecord, HostMemoryActionTargetKind, HostMemoryCommit,
  HostMemoryExclusion, HostMemoryExclusionReason, HostMemoryProcess, HostMemoryProtectionFrame,
  HostMemoryProtectionLiveStatus, HostMemoryProtectionSettings, HostMemoryProtectionStatus, HostMemoryReserveEntryStatus,
  HostMemoryReserveStatus, HostMemorySelectionBasis, HostMemoryTreeRoot, NormalRunningPoint, NormalRunningPointList,
  NormalRunningPointSummary
} from "./memoryProtectionTypes.ts";

export const memoryProtectionActionModes = ["dumpThenTerminate", "terminateOnly"] as const;
// "excluded" records are skipped process tree members (HostMemoryProtectionService tree loop); a skipped
// single-process candidate is listed in excludedCandidates, never in the records.
export const memoryProtectionActionOutcomes = ["preflight-failed", "suspend-failed", "slice-incomplete",
  "completed-with-release-limit", "completed", "terminated-without-dump", "termination-failed", "excluded"] as const;
export const memoryProtectionExclusionReasons = ["resource-manager-process", "critical-process",
  "process-identity-unavailable", "private-commit-unavailable", "baseline-private-commit-unavailable",
  "cannot-open", "identity-changed", "criticality-unavailable", "protected-process", "software-never-terminate"] as const;
export const memoryProtectionTargetKinds = ["process", "processTree"] as const;
export const memoryProtectionSelectionRules = ["process-growth-at-least-minimum", "largest-direct-children-memory-growth"] as const;
// WindowsHostMemoryProtectionExecutor: one code per failed outcome; terminated-without-dump names why no dump was
// written (process-tree is set by HostMemoryProtectionService for tree members). Completed dumps have no reason.
const actionReasonsByOutcome: Readonly<Record<Exclude<HostMemoryActionOutcome, "completed" | "excluded">, readonly HostMemoryActionReason[]>> = {
  "completed-with-release-limit": ["release-limit"],
  "slice-incomplete": ["slice-incomplete"],
  "preflight-failed": ["preflight-failed"],
  "suspend-failed": ["suspend-failed"],
  "termination-failed": ["termination-failed"],
  "terminated-without-dump": ["terminate-only", "software-no-dump", "private-commit-exceeds-reserve",
    "minidump-exceeds-reserve", "reserve-error", "reserve-unavailable", "process-tree"]
};
export const memoryProtectionActionReasons: readonly HostMemoryActionReason[] = [...new Set(Object.values(actionReasonsByOutcome).flat())];
export const memoryProtectionDecisionReasons = ["commit-unavailable", "below-threshold", "process-handled",
  "process-tree-handled", "no-positive-child-memory-growth", "no-eligible-tree"] as const;

function requireWireUInt64(value: unknown, path: string): number {
  const number = requireNonNegativeFiniteNumber(value, path);
  if (!Number.isInteger(number) || number > 18446744073709551615) {
    throw new ResponseDecodeError(path, "UInt64 JSON number");
  }
  return number;
}
// Int64 JSON numbers (minimumProcessGrowthBytes); like UInt64 fields, precision beyond 2^53 is the JSON number's.
function requireWireInt64(value: unknown, path: string): number {
  const number = requireFiniteNumber(value, path);
  if (!Number.isInteger(number) || Math.abs(number) > 9223372036854775807) {
    throw new ResponseDecodeError(path, "Int64 JSON number");
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
    })),
    minimumProcessGrowthBytes: requireWireInt64(row.minimumProcessGrowthBytes, `${path}.minimumProcessGrowthBytes`),
    protectedProcesses: requireStringArray(row.protectedProcesses, `${path}.protectedProcesses`)
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
    softwareId: nullableReason(row.softwareId, `${path}.softwareId`),
    executablePath: nullableReason(row.executablePath, `${path}.executablePath`),
    parentProcessId: requireNullable(row.parentProcessId, `${path}.parentProcessId`, requireNonNegativeSafeInteger)
  };
}
export function decodeNormalRunningPointSummary(value: unknown, path = "$"): NormalRunningPointSummary {
  const row = requireRecord(value, path);
  return { id: requireNonEmptyString(row.id, `${path}.id`), at: timestamp(row.at, `${path}.at`),
    processCount: requireNonNegativeSafeInteger(row.processCount, `${path}.processCount`),
    commit: decodeHostMemoryCommit(row.commit, `${path}.commit`) };
}
export function decodeNormalRunningPoint(value: unknown, path = "$"): NormalRunningPoint {
  const row = requireRecord(value, path);
  return { ...decodeNormalRunningPointSummary(row, path),
    processes: rows(row.processes, `${path}.processes`, decodeHostMemoryProcess) };
}
// HostMemoryProtectionService.PointList: ids are unique and the id in effect, when there is one, is a saved point.
export function decodeNormalRunningPointList(value: unknown, path = "$"): NormalRunningPointList {
  const row = requireRecord(value, path);
  const points = rows(row.points, `${path}.points`, decodeNormalRunningPointSummary);
  const ids = new Set(points.map(point => point.id));
  if (ids.size !== points.length) throw new ResponseDecodeError(`${path}.points`, "unique point ids");
  const activeId = requireNullable(row.activeId, `${path}.activeId`, requireNonEmptyString);
  if (activeId !== null && !ids.has(activeId)) throw new ResponseDecodeError(`${path}.activeId`, "the id of a saved point");
  return { activeId, points, limit: requireNonNegativeSafeInteger(row.limit, `${path}.limit`) };
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
export function decodeHostMemoryTreeRoot(value: unknown, path = "$"): HostMemoryTreeRoot {
  const row = requireRecord(value, path);
  return {
    processId: requireNonNegativeSafeInteger(row.processId, `${path}.processId`),
    processStartKey: nullableReason(row.processStartKey, `${path}.processStartKey`),
    name: nullableReason(row.name, `${path}.name`)
  };
}
function decodeSelectionBasis(value: unknown, path: string, targetKind: HostMemoryActionTargetKind): HostMemorySelectionBasis {
  const row = requireRecord(value, path);
  const rule = requireOneOf(row.rule, `${path}.rule`, memoryProtectionSelectionRules);
  const expected = targetKind === "processTree" ? "largest-direct-children-memory-growth" : "process-growth-at-least-minimum";
  if (rule !== expected) throw new ResponseDecodeError(`${path}.rule`, `${expected} for ${targetKind}`);
  // HostMemoryProtectionService fills exactly the ranking value of the rule it applied.
  const growth = (field: "processGrowthBytes" | "directChildrenMemoryGrowthBytes", applies: boolean): number | null => {
    if (applies) return requireFiniteNumber(row[field], `${path}.${field}`);
    if (row[field] !== null) throw new ResponseDecodeError(`${path}.${field}`, `null for ${rule}`);
    return null;
  };
  const tree = rule === "largest-direct-children-memory-growth";
  return {
    rule,
    processGrowthBytes: growth("processGrowthBytes", !tree),
    directChildrenMemoryGrowthBytes: growth("directChildrenMemoryGrowthBytes", tree),
    minimumProcessGrowthBytes: requireWireInt64(row.minimumProcessGrowthBytes, `${path}.minimumProcessGrowthBytes`)
  };
}
export function decodeHostMemoryActionRecord(value: unknown, path = "$"): HostMemoryActionRecord {
  const row = requireRecord(value, path);
  const outcome = requireOneOf(row.outcome, `${path}.outcome`, memoryProtectionActionOutcomes);
  const reasonPath = `${path}.reason`;
  const detail = nullableReason(row.detail, `${path}.detail`);
  let reason: HostMemoryActionReason | HostMemoryExclusionReason | null;
  if (outcome === "completed") {
    if (row.reason !== null) throw new ResponseDecodeError(reasonPath, "null for completed action");
    if (detail !== null) throw new ResponseDecodeError(`${path}.detail`, "null for completed action");
    reason = null;
  } else if (outcome === "excluded") {
    reason = requireOneOf(row.reason, reasonPath, memoryProtectionExclusionReasons);
  } else {
    reason = requireOneOf(row.reason, reasonPath, actionReasonsByOutcome[outcome]);
  }
  const targetKind = requireOneOf(row.targetKind, `${path}.targetKind`, memoryProtectionTargetKinds);
  const treeRoot = requireNullable(row.treeRoot, `${path}.treeRoot`, decodeHostMemoryTreeRoot);
  if ((targetKind === "processTree") !== (treeRoot !== null)) {
    throw new ResponseDecodeError(`${path}.treeRoot`, targetKind === "processTree" ? "tree root for processTree" : "null for process");
  }
  return {
    at: timestamp(row.at, `${path}.at`),
    processId: requireNonNegativeSafeInteger(row.processId, `${path}.processId`),
    processStartKey: requireString(row.processStartKey, `${path}.processStartKey`), name: requireString(row.name, `${path}.name`),
    baselineBytes: requireNullable(row.baselineBytes, `${path}.baselineBytes`, requireWireUInt64),
    currentBytes: requireNullable(row.currentBytes, `${path}.currentBytes`, requireWireUInt64),
    growthBytes: requireNullable(row.growthBytes, `${path}.growthBytes`, requireFiniteNumber),
    dumpPaths: requireStringArray(row.dumpPaths, `${path}.dumpPaths`),
    bytesWritten: requireWireUInt64(row.bytesWritten, `${path}.bytesWritten`), outcome, reason, detail,
    selectionBasis: decodeSelectionBasis(row.selectionBasis, `${path}.selectionBasis`, targetKind),
    actionId: requireNonEmptyString(row.actionId, `${path}.actionId`), targetKind, treeRoot,
    state: requireOneOf(row.state, `${path}.state`, ["history"] as const)
  };
}
// HostMemoryProtectionStatus.Active is "InactiveReason is null" on the backend; both are sent and must agree.
function requireActive(value: unknown, path: string, inactiveReason: string | null): boolean {
  const active = requireBoolean(value, path);
  if (active !== (inactiveReason === null)) throw new ResponseDecodeError(path, "active exactly when inactiveReason is null");
  return active;
}
export function decodeHostMemoryProtectionLiveStatus(value: unknown, path = "$"): HostMemoryProtectionLiveStatus {
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
    // Service forwards reserve.reason verbatim, including OS exception text.
    inactiveReason,
    excludedCandidates: rows(row.excludedCandidates, `${path}.excludedCandidates`, decodeHostMemoryExclusion),
    active: requireActive(row.active, `${path}.active`, inactiveReason),
    atOrAboveThreshold: requireNullable(row.atOrAboveThreshold, `${path}.atOrAboveThreshold`, requireBoolean),
    currentActionId: requireNullable(row.currentActionId, `${path}.currentActionId`, requireNonEmptyString),
    minimumProcessGrowthBytes: requireWireInt64(row.minimumProcessGrowthBytes, `${path}.minimumProcessGrowthBytes`),
    protectedProcesses: requireStringArray(row.protectedProcesses, `${path}.protectedProcesses`),
    decisionReason: requireNullable(row.decisionReason, `${path}.decisionReason`,
      (value, path) => requireOneOf(value, path, memoryProtectionDecisionReasons))
  };
}
export function decodeHostMemoryProtectionStatus(value: unknown, path = "$"): HostMemoryProtectionStatus {
  const row = requireRecord(value, path);
  return {
    ...decodeHostMemoryProtectionLiveStatus(row, path),
    normalRunningPoint: requireNullable(row.normalRunningPoint, `${path}.normalRunningPoint`, decodeNormalRunningPointSummary),
    recordCapacity: requireNonNegativeSafeInteger(row.recordCapacity, `${path}.recordCapacity`),
    unrecordedRecordCount: requireNonNegativeSafeInteger(row.unrecordedRecordCount, `${path}.unrecordedRecordCount`),
    actions: rows(row.actions, `${path}.actions`, decodeHostMemoryActionRecord)
  };
}
// HostMemoryProtectionFrames, HostMemoryProtectionPointFrame and HostMemoryProtectionPointsFrame: every subscription
// value names its frame in type. An unknown type fails the whole subscription, so tests compare this list with the backend's.
export const memoryProtectionFrameTypes = ["status", "records", "record", "normalRunningPoint", "normalRunningPoints"] as const;
export function decodeHostMemoryProtectionFrame(value: unknown, path = "$"): HostMemoryProtectionFrame {
  const row = requireRecord(value, path);
  const type = requireOneOf(row.type, `${path}.type`, memoryProtectionFrameTypes);
  switch (type) {
    case "status":
      return { type, status: decodeHostMemoryProtectionLiveStatus(row.status, `${path}.status`) };
    case "records":
      // HostMemoryProtectionRecordsFrame: limit is the record capacity.
      return { type, records: rows(row.records, `${path}.records`, decodeHostMemoryActionRecord),
        limit: requireNonNegativeSafeInteger(row.limit, `${path}.limit`),
        unrecordedRecordCount: requireNonNegativeSafeInteger(row.unrecordedRecordCount, `${path}.unrecordedRecordCount`) };
    case "record":
      return { type, record: decodeHostMemoryActionRecord(row.record, `${path}.record`) };
    case "normalRunningPoint":
      return { type,
        normalRunningPoint: requireNullable(row.normalRunningPoint, `${path}.normalRunningPoint`, decodeNormalRunningPoint) };
    case "normalRunningPoints":
      return { type, normalRunningPoints: decodeNormalRunningPointList(row.normalRunningPoints, `${path}.normalRunningPoints`) };
  }
}
export const memoryProtectionSettingsDecoder = defineResponseDecoder("memory-protection.settings.v1", decodeHostMemoryProtectionSettings);
// The reserve rebuild response is the full status; the subscription sends frames.
export const memoryProtectionStatusDecoder = defineResponseDecoder("memory-protection.status.v5", decodeHostMemoryProtectionStatus);
export const memoryProtectionFrameDecoder = defineResponseDecoder("memory-protection.frame.v4", decodeHostMemoryProtectionFrame);
export const normalRunningPointDecoder = defineResponseDecoder("memory-protection.normal-running-point.v2", decodeNormalRunningPoint);
