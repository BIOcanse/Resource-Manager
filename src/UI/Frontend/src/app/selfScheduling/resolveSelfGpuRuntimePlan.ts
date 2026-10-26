import { resolveEffectiveFontSmoothing } from "../../stores/settingsStore";
import type {
  AppAnimationMode,
  AppAppearanceSettings,
  AppFontSmoothing
} from "../../types";

export interface ResourceManagerSelfGpuRuntimePlan {
  animations: Exclude<AppAnimationMode, "auto">;
  fontSmoothing: Exclude<AppFontSmoothing, "auto">;
}

export function resolveSelfGpuRuntimePlan(
  appearance: AppAppearanceSettings | undefined,
  animations: Exclude<AppAnimationMode, "auto">
): ResourceManagerSelfGpuRuntimePlan {
  return {
    animations,
    fontSmoothing: resolveEffectiveFontSmoothing(appearance?.fontSmoothing, animations)
  };
}
