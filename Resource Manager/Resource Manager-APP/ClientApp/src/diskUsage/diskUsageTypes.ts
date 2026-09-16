/**
 * 磁盘占用页的线上类型。
 *
 * 后端只给事实：卷的容量是原始字节，介质来源和扫描方式都是稳定标识，
 * 措辞和单位换算都在前端（容量走 presentation/byteUnits.ts）。
 */

/** 介质来源，和后端 DiskUsageVolumeKinds 一一对应。 */
export type DiskUsageVolumeKind =
  | "physical"
  | "virtual"
  | "removable"
  | "network"
  | "optical"
  | "unknown";

/** 扫描范围，和后端 DiskUsageScanScopes 一一对应。 */
export type DiskUsageScanScope = "allVolumes" | "volume" | "folder";

/** 扫描方式，和后端 DiskUsageScanModes 一一对应。 */
export type DiskUsageScanMode = "fast" | "full";

/** 实际用了哪条路，和后端 DiskUsageScanKinds 一一对应。 */
export type DiskUsageScanKind = "masterFileTable" | "directoryWalk";

/** 某个目标没扫成的原因，和后端 DiskUsageSkipReasons 一一对应。 */
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
  /** 这个卷能不能走快速扫描。为 false 时界面要说清楚原因，而不是让快速扫描静悄悄漏掉它。 */
  supportsMasterFileTable: boolean;
}

export interface DiskUsageSkippedTarget {
  target: string;
  reason: DiskUsageSkipReason;
}

export interface DiskUsageScanRequest {
  scope: DiskUsageScanScope;
  mode: DiskUsageScanMode;
  /** 范围是卷时是盘符，是文件夹时是绝对路径，全局时是空串。 */
  target: string;
}
