import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireOneOf,
  requireRecord,
  requireString,
  requireStringArray,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { uiText } from "../../text.ts";
import type { DiskUsageVolume, DiskUsageVolumeKind } from "./diskUsageTypes.ts";

import type {

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

/** Fact buffers transfer directly to the page-owned worker for decoding. */
export const diskUsageTreeDecoder = defineResponseDecoder<ArrayBuffer>(
  "disk-usage.tree.v3", (value) => {
    if (!(value instanceof ArrayBuffer)) throw new ResponseDecodeError("$", "a disk tree buffer");
    return value;
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

export function getDiskUsageTree(
  requestClient: Pick<RequestClient, "request">,
  signal?: AbortSignal,
  rootIndex = 0,
  nodeId?: number,
  cursor?: number
): Promise<ArrayBuffer> {
  const parameters = new URLSearchParams();
  parameters.set("rootIndex", String(rootIndex));
  parameters.set("maxNodes", "400000");
  parameters.set("maxBytes", "33554432");
  if (nodeId !== undefined) parameters.set("node", String(nodeId));
  if (cursor !== undefined) parameters.set("cursor", String(cursor));
  return requestClient.request({
    key: "disk-usage.tree",
    url: `/api/disk-usage/tree?${parameters}`,
    fallbackError: uiText.diskUsage.emptyTitle,
    decoder: diskUsageTreeDecoder,
    signal,
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
