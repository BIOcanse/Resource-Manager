import type { GpuPlacementExactTargetOption } from "./types";
import { uiText } from "./text.ts";

export type GpuPolicySelectOption = readonly [string, string, boolean?];

// 文案按当前语言求值，不能在模块顶层固化。
function ordinarySystemTargetGpuOptions(): readonly GpuPolicySelectOption[] {
  return [
    ["SystemDefaultGpu", uiText.gpuTarget.systemDefault],
    ["HighPerformanceGpu", uiText.gpuTarget.highPerformance],
    ["IntegratedGpu", uiText.gpuTarget.integrated]
  ] as const;
}

export function targetGpuOptionsForSoftwarePolicy(
  preciseGpuPlacementEnabled: boolean,
  schedulingMode: string,
  runtimeSchedulingMode: string,
  currentTarget: string,
  exactTargets: readonly GpuPlacementExactTargetOption[] = []): readonly GpuPolicySelectOption[] {
  if (preciseGpuPlacementEnabled && schedulingMode === "Precise" && runtimeSchedulingMode === "Precise") {
    return preserveUnavailableCurrentTarget(
      [...ordinarySystemTargetGpuOptions(), ...mapExactTargets(exactTargets), ["AutoIdleGpu", uiText.gpuTarget.autoIdle]],
      currentTarget,
      uiText.gpuTarget.unavailable);
  }

  return isOrdinarySystemGpuTarget(currentTarget)
    ? ordinarySystemTargetGpuOptions()
    : [[currentTarget, uiText.gpuTarget.needsPreciseSelection(formatGpuTarget(currentTarget)), true], ...ordinarySystemTargetGpuOptions()];
}

export function startupTargetGpuOptionsForSoftwarePolicy(
  preciseGpuPlacementEnabled: boolean,
  schedulingMode: string,
  currentTarget: string,
  exactTargets: readonly GpuPlacementExactTargetOption[] = []): readonly GpuPolicySelectOption[] {
  if (isUnavailableStartupGpuTarget(currentTarget)) {
    const availableOptions = preciseGpuPlacementEnabled && schedulingMode === "Precise"
      ? [...ordinarySystemTargetGpuOptions(), ...mapExactTargets(exactTargets)]
      : ordinarySystemTargetGpuOptions();
    return [[currentTarget, uiText.gpuTarget.startupUnavailable(formatGpuTarget(currentTarget)), true], ...availableOptions];
  }

  if (preciseGpuPlacementEnabled && schedulingMode === "Precise") {
    return preserveUnavailableCurrentTarget(
      [...ordinarySystemTargetGpuOptions(), ...mapExactTargets(exactTargets)],
      currentTarget,
      uiText.gpuTarget.unavailable);
  }

  return isOrdinarySystemGpuTarget(currentTarget)
    ? ordinarySystemTargetGpuOptions()
    : [[currentTarget, uiText.gpuTarget.needsPreciseSelection(formatGpuTarget(currentTarget)), true], ...ordinarySystemTargetGpuOptions()];
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
    ? uiText.gpuTarget.autoIdle
    : target || uiText.gpuTarget.specificGpu;
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
