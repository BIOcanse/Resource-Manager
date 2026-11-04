import type { DiskUsageViewWindow } from "./diskUsageViewport.ts";

/** Frontend layout uses parallel arrays to avoid allocating an object for each tile. */
export interface DiskUsageLayout {
  rootNodeId: number;
  rootPath: string;
  rootSizeBytes: number;
  queryRootNodeId: number;
  nextCursorNodeId: number;
  /** Completeness of this page's descendants, independent of view pruning. */
  subtreeComplete: Uint8Array;
  /** Tiles omitted for size or budget limits, used to explain hidden detail. */
  omittedCount: number;
  nodeIds: Int32Array;
  parentIds: Int32Array;
  /** Tree depth, starting at zero, controls shading. */
  depths: Int32Array;
  /** Unit-space rectangles remain unchanged by viewport pan and zoom. */
  x: Float32Array;
  y: Float32Array;
  width: Float32Array;
  height: Float32Array;
  directoryFlags: Uint8Array;
  sizes: Float64Array;
  /** Names align with the layout arrays for painting and tooltips. */
  names: readonly string[];
  /** File count for directory tiles; zero for files. */
  fileCounts: Float64Array;
  /** Map node identity to layout index for selection and hover. */
  indexByNodeId: ReadonlyMap<number, number>;
  /** View used for both bitmap rendering and refinement decisions. */
  view: DiskUsageViewWindow;
}

export interface DiskUsageScanSummary {
  scope: string;
  mode: string;
  target: string;
  roots: readonly string[];
  scanKind: string;
  skipped: readonly { target: string; reason: string }[];
  completedAt: string;
  durationSeconds: number;
  totalBytes: number;
  scannedBytes: number;
  fileCount: number;
  directoryCount: number;
  skippedCount: number;
}

export interface DiskUsageNode {
  nodeId: number;
  parentNodeId: number;
  name: string;
  fullPath: string;
  isDirectory: boolean;
  sizeBytes: number;
  allocatedBytes: number;
  fileCount: number;
}
