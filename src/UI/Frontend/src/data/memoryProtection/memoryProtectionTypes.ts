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
/** One saved normal running point without its processes; the id names it for selecting and deleting. */
export interface NormalRunningPointSummary { id: string; at: string; processCount: number; commit: HostMemoryCommit; }
export interface NormalRunningPoint extends NormalRunningPointSummary {
  processes: HostMemoryProcess[];
}
/** The normalRunningPoints frame (and GET /api/memory-protection/normal-running-points): the saved points, oldest first, and the one in effect. */
export interface NormalRunningPointList {
  /** Null when no point is in effect (protection inactive with no-normal-running-point). */
  activeId: string | null;
  points: NormalRunningPointSummary[];
  /** Creation past this many saved points is refused with normal-running-point-limit. */
  limit: number;
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
  /** A record is written once its process is handled, so it is always a history report entry
   * (docs/report_lifecycle_2026-10-28.md); the action in progress is status.currentActionId. */
  state: "history";
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
  /** The record capacity (freedom point memory_protection.record_capacity); a full list writes no new records. */
  recordCapacity: number;
  /** Records not written because the list was full; clearing history resets it. */
  unrecordedRecordCount: number;
  /** All records, newest first, as sent. */
  actions: HostMemoryActionRecord[];
}
/** One value of /api/memory-protection/subscribe, dispatched on type. */
export type HostMemoryProtectionFrame =
  | { type: "status"; status: HostMemoryProtectionLiveStatus }
  | { type: "records"; records: HostMemoryActionRecord[]; /** The record capacity. */ limit: number; unrecordedRecordCount: number }
  | { type: "record"; record: HostMemoryActionRecord }
  | { type: "normalRunningPoint"; normalRunningPoint: NormalRunningPoint | null }
  | { type: "normalRunningPoints"; normalRunningPoints: NormalRunningPointList };
/** What the subscription owner folds its frames into; ready only once status, records, the point and the saved points have all arrived. */
export interface HostMemoryProtectionView {
  status: HostMemoryProtectionLiveStatus;
  /** All records, newest first. The backend writes none past recordCapacity and counts them in unrecordedRecordCount. */
  records: HostMemoryActionRecord[];
  recordCapacity: number;
  unrecordedRecordCount: number;
  /** The point in effect; null when none is in effect (none saved, or the one in effect was deleted). */
  normalRunningPoint: NormalRunningPoint | null;
  /** Every saved point and the id in effect; sent on subscribe and whenever a point is created, selected or deleted. */
  normalRunningPoints: NormalRunningPointList;
}
