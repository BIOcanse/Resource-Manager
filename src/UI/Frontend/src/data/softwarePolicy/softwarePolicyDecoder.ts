import type { GpuPlacementSoftwarePolicy } from "../../types.ts";
import {
  defineResponseDecoder, requireArray, requireBoolean, requireFiniteNumber, requireNullable,
  requireOneOf, requireRecord, requireString, requireStringArray, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

const providers = ["windows-graphics-preference", "vendor-profile", "dxgi-launch-shim", "d3d-device-create-shim",
  "vulkan-implicit-layer", "vulkan-explicit-layer", "compute-visibility"] as const;
export function decodeSoftwarePolicy(value: unknown, path = "$"): GpuPlacementSoftwarePolicy {
  const row = requireRecord(value, path);
  const target = requireString(row.startupTargetGpu, `${path}.startupTargetGpu`);
  if (!["system_default_gpu", "auto_idle_gpu", "integrated_gpu", "high_performance_gpu"].includes(target)
    && !(/^gpu\d+$/.test(target) && Number(target.slice(3)) <= 2147483647)) {
    throw new ResponseDecodeError(`${path}.startupTargetGpu`, "GPU target");
  }
  return {
    softwareId: requireString(row.softwareId, `${path}.softwareId`),
    softwareName: requireString(row.softwareName, `${path}.softwareName`),
    allowedProviders: requireArray(row.allowedProviders, `${path}.allowedProviders`)
      .map((item, index) => requireOneOf(item, `${path}.allowedProviders[${index}]`, providers)),
    startupTargetGpu: target,
    runtimeHotSwitchEnabled: requireNullable(row.runtimeHotSwitchEnabled, `${path}.runtimeHotSwitchEnabled`, requireBoolean),
    preferredRuntimeSwitchMethod: requireOneOf(row.preferredRuntimeSwitchMethod, `${path}.preferredRuntimeSwitchMethod`, ["future_frame_takeover", "window_rerender"]),
    baseScoreOverride: requireNullable(row.baseScoreOverride, `${path}.baseScoreOverride`, requireFiniteNumber),
    processOverrideAllowed: requireBoolean(row.processOverrideAllowed, `${path}.processOverrideAllowed`),
    updatedAt: requireString(row.updatedAt, `${path}.updatedAt`),
    cpuSchedulingExcluded: requireBoolean(row.cpuSchedulingExcluded, `${path}.cpuSchedulingExcluded`),
    memorySchedulingExcluded: requireBoolean(row.memorySchedulingExcluded, `${path}.memorySchedulingExcluded`),
    gpuSchedulingExcluded: requireBoolean(row.gpuSchedulingExcluded, `${path}.gpuSchedulingExcluded`),
    cpuMaximumOccupancyMode: requireOneOf(row.cpuMaximumOccupancyMode, `${path}.cpuMaximumOccupancyMode`, ["single_ccd", "all_cores"]),
    cpuManualExclusivePositionIds: requireNullable(row.cpuManualExclusivePositionIds, `${path}.cpuManualExclusivePositionIds`, requireStringArray),
    cpuManualLockedPositionIds: requireNullable(row.cpuManualLockedPositionIds, `${path}.cpuManualLockedPositionIds`, requireStringArray),
    hostMemoryProtectionNoDump: requireBoolean(row.hostMemoryProtectionNoDump, `${path}.hostMemoryProtectionNoDump`)
  };
}
export const softwarePolicyDecoder = defineResponseDecoder("software-policy.v1", decodeSoftwarePolicy);
