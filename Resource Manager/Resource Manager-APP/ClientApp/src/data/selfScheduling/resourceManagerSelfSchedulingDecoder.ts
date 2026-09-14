import type {
  ResourceManagerSelfCpuGrade,
  ResourceManagerSelfGpuGrade
} from "../../types.ts";
import {
  defineResponseDecoder,
  requireArray,
  requireNonEmptyString,
  requireRecord,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

export interface ResourceManagerSelfSchedulingSource {
  readonly targetId: string;
  readonly displayName: string;
  readonly policyId: string;
  readonly cpuGrade: ResourceManagerSelfCpuGrade;
  readonly gpuGrade: ResourceManagerSelfGpuGrade;
  readonly updatedAt: string;
  readonly reason: string;
}

export interface ResourceManagerSelfSchedulingSnapshot {
  readonly cpuGrade: ResourceManagerSelfCpuGrade;
  readonly gpuGrade: ResourceManagerSelfGpuGrade;
  readonly cpuUpdatedAt: string;
  readonly gpuUpdatedAt: string;
  readonly cpuPolicyId: string;
  readonly gpuPolicyId: string;
  readonly cpuReason: string;
  readonly gpuReason: string;
  readonly sources: readonly ResourceManagerSelfSchedulingSource[];
}

export const resourceManagerSelfSchedulingDecoder =
  defineResponseDecoder<ResourceManagerSelfSchedulingSnapshot>(
    "resource-manager.self-scheduling.v1",
    (value) => {
      const record = requireRecord(value);
      return {
        cpuGrade: requireCpuGrade(record.cpuGrade, "$.cpuGrade"),
        gpuGrade: requireGpuGrade(record.gpuGrade, "$.gpuGrade"),
        cpuUpdatedAt: requireTimestamp(record.cpuUpdatedAt, "$.cpuUpdatedAt"),
        gpuUpdatedAt: requireTimestamp(record.gpuUpdatedAt, "$.gpuUpdatedAt"),
        cpuPolicyId: requireNonEmptyString(record.cpuPolicyId, "$.cpuPolicyId"),
        gpuPolicyId: requireNonEmptyString(record.gpuPolicyId, "$.gpuPolicyId"),
        cpuReason: requireNonEmptyString(record.cpuReason, "$.cpuReason"),
        gpuReason: requireNonEmptyString(record.gpuReason, "$.gpuReason"),
        sources: requireArray(record.sources, "$.sources")
          .map((source, index) => decodeSource(source, `$.sources[${index}]`))
      };
    });

function decodeSource(
  value: unknown,
  path: string
): ResourceManagerSelfSchedulingSource {
  const record = requireRecord(value, path);
  return {
    targetId: requireNonEmptyString(record.targetId, `${path}.targetId`),
    displayName: requireNonEmptyString(record.displayName, `${path}.displayName`),
    policyId: requireNonEmptyString(record.policyId, `${path}.policyId`),
    cpuGrade: requireCpuGrade(record.cpuGrade, `${path}.cpuGrade`),
    gpuGrade: requireGpuGrade(record.gpuGrade, `${path}.gpuGrade`),
    updatedAt: requireTimestamp(record.updatedAt, `${path}.updatedAt`),
    reason: requireNonEmptyString(record.reason, `${path}.reason`)
  };
}

function requireCpuGrade(
  value: unknown,
  path: string
): ResourceManagerSelfCpuGrade {
  if (value !== "normal" && value !== "optimize") {
    throw new ResponseDecodeError(path, "'normal' or 'optimize'");
  }
  return value;
}

function requireGpuGrade(
  value: unknown,
  path: string
): ResourceManagerSelfGpuGrade {
  if (value !== "normal" && value !== "optimize") {
    throw new ResponseDecodeError(path, "'normal' or 'optimize'");
  }
  return value;
}

function requireTimestamp(value: unknown, path: string): string {
  const timestamp = requireNonEmptyString(value, path);
  if (!Number.isFinite(Date.parse(timestamp))) {
    throw new ResponseDecodeError(path, "valid timestamp string");
  }
  return timestamp;
}
