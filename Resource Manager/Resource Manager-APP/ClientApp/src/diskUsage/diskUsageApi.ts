import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireOneOf,
  requireRecord,
  requireString
} from "../frontendRuntime/request/ResponseDecoder.ts";
import type { RequestClient } from "../frontendRuntime/request/RequestClient.ts";
import { uiText } from "../text.ts";
import type { DiskUsageVolume, DiskUsageVolumeKind } from "./diskUsageTypes.ts";

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
