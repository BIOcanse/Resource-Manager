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

/** 二进制布局的头部常量。必须和 DiskUsageLayoutBinaryWriter 对上。 */
const layoutMagic = 0x5544_4d52;
const layoutVersion = 1;
const layoutHeaderBytes = 64;

/**
 * 方格布局。走二进制，不走 JSON。
 *
 * 一次布局是十来个并列数组、几万到十万项。JSON 光是序列化和解析就要好几秒，
 * 而这些本来就是定长数值 —— 这里直接在同一块 ArrayBuffer 上开定型数组视图，
 * 不逐项转换，也不为每个方格建对象。
 * 头部固定 64 字节、各列从宽到窄排，就是为了让每一列的偏移都满足对齐要求。
 */
export const diskUsageLayoutDecoder = defineResponseDecoder<DiskUsageLayout | null>(
  "disk-usage.layout.v2",
  (value) => {
    if (!(value instanceof ArrayBuffer) || value.byteLength < layoutHeaderBytes) {
      throw new ResponseDecodeError("$", "a disk usage layout buffer");
    }
    const header = new DataView(value);
    if (header.getUint32(0, true) !== layoutMagic) {
      throw new ResponseDecodeError("$.magic", "the disk usage layout magic");
    }
    if (header.getUint32(4, true) !== layoutVersion) {
      throw new ResponseDecodeError("$.version", `version ${layoutVersion}`);
    }

    const length = header.getUint32(8, true);
    const rootNodeId = header.getInt32(12, true);
    // 还没扫过：后端给的是只有头部的空布局。
    if (length === 0 || rootNodeId < 0) {
      return null;
    }

    const view = {
      minX: header.getFloat32(32, true),
      minY: header.getFloat32(36, true),
      maxX: header.getFloat32(40, true),
      maxY: header.getFloat32(44, true),
      pixelWidth: header.getFloat32(48, true),
      pixelHeight: header.getFloat32(52, true),
      scale: header.getFloat32(56, true)
    };
    const rootPathBytes = header.getUint32(60, true);

    let offset = layoutHeaderBytes;
    const sizes = new Float64Array(value, offset, length);
    offset += 8 * length;
    const nodeIds = new Int32Array(value, offset, length);
    offset += 4 * length;
    const parentIds = new Int32Array(value, offset, length);
    offset += 4 * length;
    const depths = new Int32Array(value, offset, length);
    offset += 4 * length;
    const fileCountsRaw = new Int32Array(value, offset, length);
    offset += 4 * length;
    const x = new Float32Array(value, offset, length);
    offset += 4 * length;
    const y = new Float32Array(value, offset, length);
    offset += 4 * length;
    const width = new Float32Array(value, offset, length);
    offset += 4 * length;
    const height = new Float32Array(value, offset, length);
    offset += 4 * length;
    const directoryFlags = new Uint8Array(value, offset, length);
    offset += length;
    // 名字那几列之前补齐到 4 的倍数，否则 Uint32Array 开不出来。
    offset += (4 - (offset % 4)) % 4;
    const nameByteLengths = new Uint32Array(value, offset, length);
    offset += 4 * length;

    const decoder = new TextDecoder();
    const names = new Array<string>(length);
    const indexByNodeId = new Map<number, number>();
    const fileCounts = new Float64Array(length);
    for (let index = 0; index < length; index++) {
      const nameLength = nameByteLengths[index];
      names[index] = nameLength === 0
        ? ""
        : decoder.decode(new Uint8Array(value, offset, nameLength));
      offset += nameLength;
      indexByNodeId.set(nodeIds[index], index);
      fileCounts[index] = fileCountsRaw[index];
    }

    return {
      rootNodeId,
      rootPath: rootPathBytes === 0
        ? ""
        : decoder.decode(new Uint8Array(value, offset, rootPathBytes)),
      rootSizeBytes: header.getFloat64(24, true),
      omittedCount: header.getUint32(16, true),
      nodeIds,
      parentIds,
      depths,
      x,
      y,
      width,
      height,
      directoryFlags,
      sizes,
      names,
      fileCounts,
      indexByNodeId,
      view
    };
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
    // 方格数据走二进制，见 diskUsageLayoutDecoder。
    request: { method: "GET", binary: true }
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
