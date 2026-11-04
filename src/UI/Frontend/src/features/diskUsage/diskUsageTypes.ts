/** Backend wire facts retain raw byte capacities and stable identifiers; the frontend owns wording and unit formatting. */

/** Media-source identifiers match backend DiskUsageVolumeKinds. */
export type DiskUsageVolumeKind =
  | "physical"
  | "virtual"
  | "external"
  | "network"
  | "optical"
  | "unknown";

/** Scan-scope identifiers match backend DiskUsageScanScopes. */
export type DiskUsageScanScope = "allVolumes" | "volume" | "folder";

/** Requested scan-mode identifiers match backend DiskUsageScanModes. */
export type DiskUsageScanMode = "fast" | "full";

/** Actual scan-path identifiers match backend DiskUsageScanKinds. */
export type DiskUsageScanKind = "masterFileTable" | "directoryWalk";

/** Skipped-target reasons match backend DiskUsageSkipReasons. */
export type DiskUsageSkipReason =
  | "noFileSystemIndex"
  | "needsElevation"
  | "volumeNotReady"
  | "targetUnavailable";

export interface DiskUsageVolume {
  volumeId: string;
  label: string;
  fileSystem: string;
  volumeKind: DiskUsageVolumeKind;
  totalBytes: number;
  freeBytes: number;
  isReady: boolean;
  /** Fast-scan availability requires an explanatory reason when false. */
  supportsMasterFileTable: boolean;
}

export interface DiskUsageSkippedTarget {
  target: string;
  reason: DiskUsageSkipReason;
}

export interface DiskUsageScanRequest {
  scope: DiskUsageScanScope;
  mode: DiskUsageScanMode;
  /** Volume scope uses a drive letter, folder scope an absolute path, and global scope an empty string. */
  target: string;
}
