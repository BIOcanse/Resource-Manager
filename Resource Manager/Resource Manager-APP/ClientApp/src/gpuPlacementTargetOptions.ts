import type { GpuPlacementExactTargetOption } from "./types";

export type GpuPolicySelectOption = readonly [string, string, boolean?];

const ordinarySystemTargetGpuOptions: readonly GpuPolicySelectOption[] = [
  ["SystemDefaultGpu", "自动选择"],
  ["HighPerformanceGpu", "高性能显卡"],
  ["IntegratedGpu", "低功耗显卡"]
] as const;

export function targetGpuOptionsForSoftwarePolicy(
  preciseGpuPlacementEnabled: boolean,
  schedulingMode: string,
  runtimeSchedulingMode: string,
  currentTarget: string,
  exactTargets: readonly GpuPlacementExactTargetOption[] = []): readonly GpuPolicySelectOption[] {
  if (preciseGpuPlacementEnabled && schedulingMode === "Precise" && runtimeSchedulingMode === "Precise") {
    return preserveUnavailableCurrentTarget(
      [...ordinarySystemTargetGpuOptions, ...mapExactTargets(exactTargets), ["AutoIdleGpu", "自动选择空闲显卡"]],
      currentTarget,
      "当前不可用");
  }

  return isOrdinarySystemGpuTarget(currentTarget)
    ? ordinarySystemTargetGpuOptions
    : [[currentTarget, `${formatGpuTarget(currentTarget)}（需要开启精确 GPU 选择）`, true], ...ordinarySystemTargetGpuOptions];
}

export function startupTargetGpuOptionsForSoftwarePolicy(
  preciseGpuPlacementEnabled: boolean,
  schedulingMode: string,
  currentTarget: string,
  exactTargets: readonly GpuPlacementExactTargetOption[] = []): readonly GpuPolicySelectOption[] {
  if (isUnavailableStartupGpuTarget(currentTarget)) {
    const availableOptions = preciseGpuPlacementEnabled && schedulingMode === "Precise"
      ? [...ordinarySystemTargetGpuOptions, ...mapExactTargets(exactTargets)]
      : ordinarySystemTargetGpuOptions;
    return [[currentTarget, `${formatGpuTarget(currentTarget)}（启动期暂不可用）`, true], ...availableOptions];
  }

  if (preciseGpuPlacementEnabled && schedulingMode === "Precise") {
    return preserveUnavailableCurrentTarget(
      [...ordinarySystemTargetGpuOptions, ...mapExactTargets(exactTargets)],
      currentTarget,
      "当前不可用");
  }

  return isOrdinarySystemGpuTarget(currentTarget)
    ? ordinarySystemTargetGpuOptions
    : [[currentTarget, `${formatGpuTarget(currentTarget)}（需要开启精确 GPU 选择）`, true], ...ordinarySystemTargetGpuOptions];
}

export function isOrdinarySystemGpuTarget(target: string) {
  return target === "SystemDefaultGpu" || target === "IntegratedGpu" || target === "HighPerformanceGpu";
}

export function isUnavailableStartupGpuTarget(target: string | undefined | null) {
  return target === "AutoIdleGpu";
}

export function toOrdinarySystemGpuTarget(target: string | undefined | null) {
  return target === "IntegratedGpu" || target === "HighPerformanceGpu"
    ? target
    : "SystemDefaultGpu";
}

function formatGpuTarget(target: string) {
  return target === "AutoIdleGpu"
    ? "自动选择空闲显卡"
    : target || "指定显卡";
}

function mapExactTargets(
  exactTargets: readonly GpuPlacementExactTargetOption[]): GpuPolicySelectOption[] {
  return exactTargets.map((target) => [
    target.targetGpu,
    target.available ? target.displayName : `${target.displayName}${target.reason ? ` · ${target.reason}` : ""}`,
    !target.available
  ]);
}

function preserveUnavailableCurrentTarget(
  options: readonly GpuPolicySelectOption[],
  currentTarget: string,
  reason: string): readonly GpuPolicySelectOption[] {
  if (!isExactGpuTarget(currentTarget)
      || options.some(([value]) => value.toLowerCase() === currentTarget.toLowerCase())) {
    return options;
  }

  return [[currentTarget, `${formatGpuTarget(currentTarget)}（${reason}）`, true], ...options];
}

function isExactGpuTarget(target: string) {
  return /^GPU\d+$/i.test(target);
}
