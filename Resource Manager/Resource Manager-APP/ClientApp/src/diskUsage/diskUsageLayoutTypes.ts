/**
 * 方格布局的线上形状。
 *
 * 方格是这条线上量最大的东西（一次可能几万个），所以发成并列数组而不是一堆对象：
 * 同一个字段连续存放，解码和绘制都能顺着走，不用为每个方格建一个对象。
 */
export interface DiskUsageLayout {
  rootNodeId: number;
  rootPath: string;
  rootSizeBytes: number;
  /** 因为太小或超预算而没有单独画出来的方格数。界面据此提示"还有更小的没画"。 */
  omittedCount: number;
  nodeIds: Int32Array;
  parentIds: Int32Array;
  /** 方格在树里的深度，根为 0。画的时候用它做明度分层。 */
  depths: Int32Array;
  /** 单位空间 [0,1] 里的矩形。缩放平移只是视口变换，不改这些值。 */
  x: Float32Array;
  y: Float32Array;
  width: Float32Array;
  height: Float32Array;
  directoryFlags: Uint8Array;
  sizes: Float64Array;
  /** 每个方格的名字，跟上面几个数组同序。画名字和悬停提示都直接读它。 */
  names: readonly string[];
  /** 目录方格里有多少个文件。文件方格是 0。 */
  fileCounts: Float64Array;
  /** 节点序号 → 上面数组里的下标。选中和悬停要按节点序号找回方格。 */
  indexByNodeId: ReadonlyMap<number, number>;
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
