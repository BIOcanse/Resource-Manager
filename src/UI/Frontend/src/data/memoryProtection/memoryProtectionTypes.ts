// UInt64 and Decimal byte fields in this API are JSON numbers, not decimal strings.
export type HostMemoryProtectionActionMode = "dumpThenTerminate" | "terminateOnly";
export type HostMemoryActionOutcome = "preflight-failed" | "suspend-failed" | "slice-incomplete"
  | "completed-with-release-limit" | "completed" | "terminated-without-dump" | "termination-failed";
export type HostMemoryExclusionReason = "resource-manager-process" | "critical-process"
  | "process-identity-unavailable" | "private-commit-unavailable" | "baseline-private-commit-unavailable"
  | "cannot-open" | "identity-changed" | "criticality-unavailable";
export type HostMemoryDirectActionReason = "terminate-only" | "software-no-dump"
  | "private-commit-exceeds-reserve" | "minidump-exceeds-reserve";

export interface HostMemoryReserveSettings { location: string; sizeBytes: number; }
export interface HostMemoryProtectionSettings {
  enabled: boolean;
  thresholdPercent: number;
  checkIntervalSeconds: number;
  reserves: HostMemoryReserveSettings[] | null;
  actionMode: HostMemoryProtectionActionMode;
}
// AppSettings PATCH forbids removing a field with null; reserves replaces the full ordered array.
export type HostMemoryProtectionSettingsPatch = Partial<Omit<HostMemoryProtectionSettings, "reserves">>
  & { reserves?: HostMemoryReserveSettings[] };
export interface HostMemoryCommit { currentBytes: number; limitBytes: number; percent: number; }
export interface HostMemoryProcess {
  processId: number;
  processStartKey: string;
  name: string;
  privateCommitBytes: number | null;
  softwareId: string | null;
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
export interface HostMemoryActionRecord {
  at: string;
  processId: number;
  processStartKey: string;
  name: string;
  baselineBytes: number;
  currentBytes: number;
  growthBytes: number;
  dumpPaths: string[];
  bytesWritten: number;
  outcome: HostMemoryActionOutcome;
  reason: string | null;
}
export interface HostMemoryProtectionStatus {
  enabled: boolean;
  thresholdPercent: number;
  checkIntervalSeconds: number;
  actionMode: HostMemoryProtectionActionMode;
  reserve: HostMemoryReserveStatus;
  triggerBytes: number | null;
  /** Commit headroom, possibly negative; distinct from reserve.remainingBytes. */
  remainingBytes: number | null;
  currentCommit: HostMemoryCommit | null;
  normalRunningPoint: NormalRunningPointSummary | null;
  /** Includes reserve.reason, which can be an arbitrary OS/IO diagnostic. */
  inactiveReason: string | null;
  excludedCandidates: HostMemoryExclusion[];
  actionRecordLimit: number;
  actions: HostMemoryActionRecord[];
}
