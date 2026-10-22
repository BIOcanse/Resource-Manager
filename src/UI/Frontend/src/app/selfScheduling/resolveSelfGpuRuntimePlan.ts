import {
  resolveEffectiveAnimationMode,
  resolveEffectiveFontSmoothing
} from "../../stores/settingsStore";
import type {
  AppAnimationMode,
  AppAppearanceSettings,
  AppFontSmoothing,
  ResourceManagerSelfGpuGrade
} from "../../types";

export interface ResourceManagerSelfGpuRuntimePlan {
  animations: Exclude<AppAnimationMode, "auto">;
  fontSmoothing: Exclude<AppFontSmoothing, "auto">;
}

export function resolveSelfGpuRuntimePlan(
  appearance: AppAppearanceSettings | undefined,
  gpuGrade: ResourceManagerSelfGpuGrade,
  foregroundInteractive: boolean
): ResourceManagerSelfGpuRuntimePlan {
  const animations = resolveEffectiveAnimationMode(
    appearance?.animations,
    gpuGrade,
    foregroundInteractive);
  return {
    animations,
    fontSmoothing: resolveEffectiveFontSmoothing(appearance?.fontSmoothing, animations)
  };
}
