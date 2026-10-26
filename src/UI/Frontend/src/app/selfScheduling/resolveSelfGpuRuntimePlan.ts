import { resolveEffectiveFontSmoothing } from "../../stores/settingsStore";
import type { AnimationStyleId } from "../../presentation/animationStyle.ts";
import type {
  AppAppearanceSettings,
  AppFontSmoothing
} from "../../types";

export interface ResourceManagerSelfGpuRuntimePlan {
  /** 实际生效的动画风格（自动关闭选过之后）。 */
  animations: AnimationStyleId;
  fontSmoothing: Exclude<AppFontSmoothing, "auto">;
}

export function resolveSelfGpuRuntimePlan(
  appearance: AppAppearanceSettings | undefined,
  animations: AnimationStyleId
): ResourceManagerSelfGpuRuntimePlan {
  return {
    animations,
    fontSmoothing: resolveEffectiveFontSmoothing(appearance?.fontSmoothing, animations)
  };
}
