import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireOneOf,
  requireRecord,
  requireSafeInteger,
  requireString,
  requireStringArray,
  ResponseDecodeError
} from "../frontendRuntime/request/ResponseDecoder.ts";
import type { RequestClient } from "../frontendRuntime/request/RequestClient.ts";
import { uiText } from "../text.ts";
import type { DiskUsageVolume, DiskUsageVolumeKind } from "./diskUsageTypes.ts";
import type { DiskUsageViewWindow } from "./diskUsageViewport.ts";
import type {
  DiskUsageLayout,
  DiskUsageNode,
  DiskUsageScanSummary
} from "./diskUsageLayoutTypes.ts";

const volumeKinds = [
  "physical",
  "virtual",
  "removable",
  "network",
  "optical",
  "unknown"
] as const satisfies readonly DiskUsageVolumeKind[];

export const diskUsageVolumesDecoder = defineResponseDecoder<DiskUsageVolume[]>(
  "disk-usage.volumes.v1",
  (value) => requireArray(value, "$").map((row, index) => {
    const record = requireRecord(row, `$[${index}]`);
    return {
      volumeId: requireNonEmptyString(record.volumeId, `$[${index}].volumeId`),
      // 卷标可以为空，前端出兜底名。
      label: requireString(record.label, `$[${index}].label`),
      fileSystem: requireString(record.fileSystem, `$[${index}].fileSystem`),
      volumeKind: requireOneOf(record.volumeKind, `$[${index}].volumeKind`, volumeKinds),
      totalBytes: requireNonNegativeSafeInteger(record.totalBytes, `$[${index}].totalBytes`),
      freeBytes: requireNonNegativeSafeInteger(record.freeBytes, `$[${index}].freeBytes`),
      isReady: requireBoolean(record.isReady, `$[${index}].isReady`),
      supportsMasterFileTable: requireBoolean(
        record.supportsMasterFileTable,
        `$[${index}].supportsMasterFileTable`)
    };
  }));

export function getDiskUsageVolumes(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<DiskUsageVolume[]> {
  return requestClient.request({
    key: "disk-usage.volumes",
    url: "/api/disk-usage/volumes",
    fallbackError: uiText.diskUsage.noVolumes,
    decoder: diskUsageVolumesDecoder,
    signal,
    request: { method: "GET" }
  });
}


/** 概况。还没扫过时后端返回空，解码成 null。 */
export const diskUsageSummaryDecoder =
  defineResponseDecoder<DiskUsageScanSummary | null>(
    "disk-usage.summary.v1",
    (value) => {
      if (value === null || value === undefined) {
        return null;
      }
      const record = requireRecord(value, "$");
      return {
        scope: requireNonEmptyString(record.scope, "$.scope"),
        mode: requireNonEmptyString(record.mode, "$.mode"),
        target: requireString(record.target, "$.target"),
        roots: requireStringArray(record.roots, "$.roots"),
        scanKind: requireNonEmptyString(record.scanKind, "$.scanKind"),
        skipped: requireArray(record.skipped, "$.skipped").map((row, index) => {
          const entry = requireRecord(row, `$.skipped[${index}]`);
          return {
            target: requireString(entry.target, `$.skipped[${index}].target`),
            reason: requireNonEmptyString(entry.reason, `$.skipped[${index}].reason`)
          };
        }),
        completedAt: requireNonEmptyString(record.completedAt, "$.completedAt"),
        durationSeconds: requireFiniteNumber(record.durationSeconds, "$.durationSeconds"),
        totalBytes: requireNonNegativeSafeInteger(record.totalBytes, "$.totalBytes"),
        scannedBytes: requireNonNegativeSafeInteger(record.scannedBytes, "$.scannedBytes"),
        fileCount: requireNonNegativeSafeInteger(record.fileCount, "$.fileCount"),
        directoryCount: requireNonNegativeSafeInteger(
          record.directoryCount,
          "$.directoryCount"),
        skippedCount: requireNonNegativeSafeInteger(record.skippedCount, "$.skippedCount")
      };
    });

/**
 * 方格布局。方格按并列数组过来，这里直接转成定型数组：
 * 绘制是热路径，一次可能几万个方格，不为每个方格建对象。
 */
export const diskUsageLayoutDecoder = defineResponseDecoder<DiskUsageLayout | null>(
  "disk-usage.layout.v1",
  (value) => {
    if (value === null || value === undefined) {
      return null;
    }
    const record = requireRecord(value, "$");
    const nodeIds = requireArray(record.nodeIds, "$.nodeIds");
    const length = nodeIds.length;
    const requireColumn = (raw: unknown, path: string) => {
      const column = requireArray(raw, path);
      if (column.length !== length) {
        throw new ResponseDecodeError(path, `column of ${length} values`);
      }
      return column;
    };

    const parentIds = requireColumn(record.parentIds, "$.parentIds");
    const depths = requireColumn(record.depths, "$.depths");
    const x = requireColumn(record.x, "$.x");
    const y = requireColumn(record.y, "$.y");
    const width = requireColumn(record.width, "$.width");
    const height = requireColumn(record.height, "$.height");
    const directoryFlags = requireColumn(record.directoryFlags, "$.directoryFlags");
    const sizes = requireColumn(record.sizes, "$.sizes");
    const names = requireColumn(record.names, "$.names");
    const fileCounts = requireColumn(record.fileCounts, "$.fileCounts");

    const decodedNames = new Array<string>(length);
    const indexByNodeId = new Map<number, number>();
    const layout: DiskUsageLayout = {
      rootNodeId: requireSafeInteger(record.rootNodeId, "$.rootNodeId"),
      rootPath: requireString(record.rootPath, "$.rootPath"),
      rootSizeBytes: requireNonNegativeSafeInteger(
        record.rootSizeBytes,
        "$.rootSizeBytes"),
      omittedCount: requireNonNegativeSafeInteger(record.omittedCount, "$.omittedCount"),
      nodeIds: new Int32Array(length),
      parentIds: new Int32Array(length),
      depths: new Int32Array(length),
      x: new Float32Array(length),
      y: new Float32Array(length),
      width: new Float32Array(length),
      height: new Float32Array(length),
      directoryFlags: new Uint8Array(length),
      sizes: new Float64Array(length),
      names: decodedNames,
      fileCounts: new Float64Array(length),
      indexByNodeId
    };

    for (let index = 0; index < length; index++) {
      layout.nodeIds[index] = requireSafeInteger(nodeIds[index], `$.nodeIds[${index}]`);
      indexByNodeId.set(layout.nodeIds[index], index);
      decodedNames[index] = requireString(names[index], `$.names[${index}]`);
      layout.parentIds[index] = requireSafeInteger(
        parentIds[index],
        `$.parentIds[${index}]`);
      layout.depths[index] = requireSafeInteger(depths[index], `$.depths[${index}]`);
      layout.x[index] = requireFiniteNumber(x[index], `$.x[${index}]`);
      layout.y[index] = requireFiniteNumber(y[index], `$.y[${index}]`);
      layout.width[index] = requireFiniteNumber(width[index], `$.width[${index}]`);
      layout.height[index] = requireFiniteNumber(height[index], `$.height[${index}]`);
      layout.directoryFlags[index] = requireBoolean(
        directoryFlags[index],
        `$.directoryFlags[${index}]`) ? 1 : 0;
      layout.sizes[index] = requireFiniteNumber(sizes[index], `$.sizes[${index}]`);
      layout.fileCounts[index] = requireNonNegativeSafeInteger(
        fileCounts[index],
        `$.fileCounts[${index}]`);
    }
    return layout;
  });

export const diskUsageNodeDecoder = defineResponseDecoder<DiskUsageNode>(
  "disk-usage.node.v1",
  (value) => {
    const record = requireRecord(value, "$");
    return {
      nodeId: requireNonNegativeSafeInteger(record.nodeId, "$.nodeId"),
      parentNodeId: requireNonNegativeSafeInteger(record.parentNodeId, "$.parentNodeId"),
      name: requireString(record.name, "$.name"),
      fullPath: requireString(record.fullPath, "$.fullPath"),
      isDirectory: requireBoolean(record.isDirectory, "$.isDirectory"),
      sizeBytes: requireNonNegativeSafeInteger(record.sizeBytes, "$.sizeBytes"),
      allocatedBytes: requireNonNegativeSafeInteger(
        record.allocatedBytes,
        "$.allocatedBytes"),
      fileCount: requireNonNegativeSafeInteger(record.fileCount, "$.fileCount")
    };
  });

export function getDiskUsageSummary(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal
): Promise<DiskUsageScanSummary | null> {
  return requestClient.request({
    key: "disk-usage.summary",
    url: "/api/disk-usage/summary",
    fallbackError: uiText.diskUsage.emptyTitle,
    decoder: diskUsageSummaryDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function getDiskUsageLayout(
  requestClient: Pick<RequestClient, "request">,
  nodeId?: number,
  signal?: AbortSignal,
  view?: DiskUsageViewWindow
): Promise<DiskUsageLayout | null> {
  const parameters = new URLSearchParams();
  if (typeof nodeId === "number") {
    parameters.set("node", String(nodeId));
  }
  if (view) {
    parameters.set("pixelWidth", view.pixelWidth.toFixed(1));
    parameters.set("pixelHeight", view.pixelHeight.toFixed(1));
    parameters.set("scale", view.scale.toFixed(4));
    parameters.set("minX", view.minX.toFixed(6));
    parameters.set("minY", view.minY.toFixed(6));
    parameters.set("maxX", view.maxX.toFixed(6));
    parameters.set("maxY", view.maxY.toFixed(6));
  }
  const query = parameters.size > 0 ? `?${parameters}` : "";
  return requestClient.request({
    key: "disk-usage.layout",
    url: `/api/disk-usage/layout${query}`,
    fallbackError: uiText.diskUsage.emptyTitle,
    decoder: diskUsageLayoutDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function getDiskUsageNode(
  requestClient: Pick<RequestClient, "request">,
  nodeId: number,
  signal?: AbortSignal
): Promise<DiskUsageNode> {
  return requestClient.request({
    key: "disk-usage.node",
    url: `/api/disk-usage/node/${nodeId}`,
    fallbackError: uiText.diskUsage.emptyTitle,
    decoder: diskUsageNodeDecoder,
    signal,
    request: { method: "GET" }
  });
}
