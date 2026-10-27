// UInt64 and Decimal byte fields in this API are JSON numbers, not decimal strings.
export type HostMemoryProtectionActionMode = "dumpThenTerminate" | "terminateOnly";
/** "excluded" is a skipped member of a process tree action; its reason is an exclusion reason. */
export type HostMemoryActionOutcome = "preflight-failed" | "suspend-failed" | "slice-incomplete"
  | "completed-with-release-limit" | "completed" | "terminated-without-dump" | "termination-failed" | "excluded";
export type HostMemoryExclusionReason = "resource-manager-process" | "critical-process"
  | "process-identity-unavailable" | "private-commit-unavailable" | "baseline-private-commit-unavailable"
  | "cannot-open" | "identity-changed" | "criticality-unavailable" | "protected-process" | "software-never-terminate";
/** Action reason codes for every outcome except completed (null) and excluded (an exclusion reason). */
export type HostMemoryActionReason = "release-limit" | "slice-incomplete" | "preflight-failed" | "suspend-failed"
  | "termination-failed" | "terminate-only" | "software-no-dump" | "private-commit-exceeds-reserve"
  | "minidump-exceeds-reserve" | "reserve-error" | "reserve-unavailable" | "process-tree";
export type HostMemoryActionTargetKind = "process" | "processTree";
export type HostMemorySelectionRule = "process-growth-at-least-minimum" | "largest-direct-children-memory-growth";
/** processGrowthBytes is non-null exactly for the single-process rule, directChildrenMemoryGrowthBytes for the tree rule. */
export interface HostMemorySelectionBasis {
  rule: HostMemorySelectionRule;
  processGrowthBytes: number | null;
  directChildrenMemoryGrowthBytes: number | null;
  /** The setting value this decision compared with. */
  minimumProcessGrowthBytes: number;
}
/** Why the last delivery at or above the threshold (or below it) ended the way it did. */
export type HostMemoryDecisionReason = "commit-unavailable" | "below-threshold" | "process-handled"
  | "process-tree-handled" | "no-positive-child-memory-growth" | "no-eligible-tree";

export interface HostMemoryReserveSettings { location: string; sizeBytes: number; }
export interface HostMemoryProtectionSettings {
  enabled: boolean;
  thresholdPercent: number;
  checkIntervalSeconds: number;
  reserves: HostMemoryReserveSettings[] | null;
  actionMode: HostMemoryProtectionActionMode;
  /** Signed 64-bit byte count on the wire; values outside the safe integer range are not representable here. */
  minimumProcessGrowthBytes: number;
  /** Executable names or full paths, sent exactly as entered; an empty list is a valid setting. */
  protectedProcesses: string[];
}
export interface HostMemoryCommit { currentBytes: number; limitBytes: number; percent: number; }
export interface HostMemoryProcess {
  processId: number;
  processStartKey: string;
  name: string;
  privateCommitBytes: number | null;
  softwareId: string | null;
  executablePath: string | null;
  parentProcessId: number | null;
}
export interface NormalRunningPointSummary { at: string; processCount: number; }
export interface NormalRunningPoint extends NormalRunningPointSummary {
  commit: HostMemoryCommit;
  processes: HostMemoryProcess[];
}
export interface HostMemoryReserveEntryStatus {
  location: string;
  configuredSizeBytes: number;
  built: boolean;
  // Backend IO/native exception messages are open strings, not a closed enum.
  reason: string | null;
  remainingBytes: number;
  clusterBytes: number;
}
export interface HostMemoryReserveStatus {
  entries: HostMemoryReserveEntryStatus[];
  configuredSizeBytes: number;
  built: boolean;
  reason: string | null;
  remainingBytes: number;
}
export interface HostMemoryExclusion {
  processId: number; processStartKey: string; name: string; reason: HostMemoryExclusionReason;
}
/** An exited root keeps its PID; its start key and name are null when the point does not have them. */
export interface HostMemoryTreeRoot { processId: number; processStartKey: string | null; name: string | null; }
export interface HostMemoryActionRecord {
  at: string;
  processId: number;
  processStartKey: string;
  name: string;
  /** Null when the backend could not read the value (skipped tree members). */
  baselineBytes: number | null;
  currentBytes: number | null;
  growthBytes: number | null;
  dumpPaths: string[];
  bytesWritten: number;
  outcome: HostMemoryActionOutcome;
  /** A stable code; null exactly for a completed dump. Excluded records carry an exclusion reason. */
  reason: HostMemoryActionReason | HostMemoryExclusionReason | null;
  /** Diagnostic text sent with the code (native status, IO message, release limit), shown as sent. */
  detail: string | null;
  /** The decision that chose this action; every record of one action carries the same basis. */
  selectionBasis: HostMemorySelectionBasis;
  /** All records of one handled target share this id; a process tree has one record per member. */
  actionId: string;
  targetKind: HostMemoryActionTargetKind;
  /** Non-null exactly for processTree records. */
  treeRoot: HostMemoryTreeRoot | null;
}
/** The sampled status frame of the subscription; records and the point have their own frames. */
export interface HostMemoryProtectionLiveStatus {
  enabled: boolean;
  thresholdPercent: number;
  checkIntervalSeconds: number;
  actionMode: HostMemoryProtectionActionMode;
  reserve: HostMemoryReserveStatus;
  triggerBytes: number | null;
  /** Commit headroom, possibly negative; distinct from reserve.remainingBytes. */
  remainingBytes: number | null;
  currentCommit: HostMemoryCommit | null;
  /** Includes reserve.reason, which can be an arbitrary OS/IO diagnostic. */
  inactiveReason: string | null;
  excludedCandidates: HostMemoryExclusion[];
  /** inactiveReason is null. Independent of reserve.built: a run-time reserve error keeps protection active. */
  active: boolean;
  /** Null when commit charge is unreadable. */
  atOrAboveThreshold: boolean | null;
  currentActionId: string | null;
  minimumProcessGrowthBytes: number;
  protectedProcesses: string[];
  decisionReason: HostMemoryDecisionReason | null;
}
/** GET /api/memory-protection/status and the reserve rebuild response. */
export interface HostMemoryProtectionStatus extends HostMemoryProtectionLiveStatus {
  normalRunningPoint: NormalRunningPointSummary | null;
  actionRecordLimit: number;
  /** Newest first, as sent. */
  actions: HostMemoryActionRecord[];
}
/** One value of /api/memory-protection/subscribe, dispatched on type. */
export type HostMemoryProtectionFrame =
  | { type: "status"; status: HostMemoryProtectionLiveStatus }
  | { type: "records"; records: HostMemoryActionRecord[]; limit: number }
  | { type: "record"; record: HostMemoryActionRecord }
  | { type: "normalRunningPoint"; normalRunningPoint: NormalRunningPoint | null };
/** What the subscription owner folds its frames into; ready only once status, records and point have all arrived. */
export interface HostMemoryProtectionView {
  status: HostMemoryProtectionLiveStatus;
  /** Newest first, at most recordLimit. */
  records: HostMemoryActionRecord[];
  recordLimit: number;
  /** Null when no point exists (never created or cleared). */
  normalRunningPoint: NormalRunningPoint | null;
}
