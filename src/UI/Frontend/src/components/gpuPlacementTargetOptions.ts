import type { GpuPlacementExactTargetOption } from "../types";
import { uiText } from "../text.ts";

export type GpuPolicySelectOption = readonly [string, string, boolean?];

/** Unknown requires runtime validation; it does not prevent saving a user's choice. */
export function canConfigureRuntimePlacement(state: string | null | undefined): boolean {
  return state === "supported" || state === "unknown";
}

// 文案按当前语言求值，不能在模块顶层固化。
function ordinarySystemTargetGpuOptions(): readonly GpuPolicySelectOption[] {
  return [
    ["system_default_gpu", uiText.gpuTarget.systemDefault],
    ["high_performance_gpu", uiText.gpuTarget.highPerformance],
    ["integrated_gpu", uiText.gpuTarget.integrated]
  ] as const;
}

/**
 * preciseSelectionAvailable：调用方传入“精确调度已开启且启动期能力可用”，
 * 为 true 时才列出具体显卡，否则只给系统级目标。
 */
export function startupTargetGpuOptionsForSoftwarePolicy(
  preciseSelectionAvailable: boolean,
  currentTarget: string,
  exactTargets: readonly GpuPlacementExactTargetOption[] = []): readonly GpuPolicySelectOption[] {
  if (isUnavailableStartupGpuTarget(currentTarget)) {
    const availableOptions = preciseSelectionAvailable
      ? [...ordinarySystemTargetGpuOptions(), ...mapExactTargets(exactTargets)]
      : ordinarySystemTargetGpuOptions();
    return [[currentTarget, uiText.gpuTarget.startupUnavailable(formatGpuTarget(currentTarget)), true], ...availableOptions];
  }

  if (preciseSelectionAvailable) {
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
  return target === "system_default_gpu" || target === "integrated_gpu" || target === "high_performance_gpu";
}

export function isUnavailableStartupGpuTarget(target: string | undefined | null) {
  return target === "auto_idle_gpu";
}

export function toOrdinarySystemGpuTarget(target: string | undefined | null) {
  return target === "integrated_gpu" || target === "high_performance_gpu"
    ? target
    : "system_default_gpu";
}

function formatGpuTarget(target: string) {
  return target === "auto_idle_gpu"
    ? uiText.gpuTarget.autoIdle
    : /^gpu\d+$/.test(target) ? target.toLocaleUpperCase() : target || uiText.gpuTarget.specificGpu;
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
      || options.some(([value]) => value === currentTarget)) {
    return options;
  }

  return [[currentTarget, `${formatGpuTarget(currentTarget)}（${reason}）`, true], ...options];
}

function isExactGpuTarget(target: string) {
  return /^gpu\d+$/.test(target);
}
