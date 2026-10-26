import { createComputed, createSignal, untrack, type Accessor } from "solid-js";
import type { AppAnimationMode, ResourceManagerSelfGpuGrade } from "../../types.ts";

export type EffectiveAnimationMode = Exclude<AppAnimationMode, "auto">;

export const AUTO_ANIMATION_RECENT_INPUT_MS = 5_000;
// The user's example for a strained machine is no animation (docs/automatic_animation_selection_2026-10-25.md).
export const AUTO_ANIMATION_OPTIMIZE_MODE: EffectiveAnimationMode = "none";

/** Owns the resolved tier. Only a foreground entry may sample the auto grade. */
export function createAnimationModeSelection(options: {
  mode: Accessor<AppAnimationMode>;
  foreground: Accessor<boolean>;
  gpuGrade: Accessor<ResourceManagerSelfGpuGrade>;
  now: () => number;
}) {
  // Before the first foreground entry, auto explicitly starts with normal motion.
  const [effectiveMode, setEffectiveMode] = createSignal<EffectiveAnimationMode>("normal");
  let wasForeground = false;
  let lastInputAt = Number.NEGATIVE_INFINITY;

  createComputed(() => {
    const mode = options.mode();
    const foreground = options.foreground();
    const returnedToForeground = foreground && !wasForeground;
    wasForeground = foreground;
    if (mode !== "auto") {
      setEffectiveMode(mode);
    } else if (returnedToForeground && options.now() - lastInputAt >= AUTO_ANIMATION_RECENT_INPUT_MS) {
      setEffectiveMode(untrack(options.gpuGrade) === "optimize"
        ? AUTO_ANIMATION_OPTIMIZE_MODE
        : "normal");
    }
  });

  return {
    effectiveMode,
    // Activity is input while already in the foreground. Input that arrives before the foreground
    // signal, such as the click that activates the window, belongs to the return and does not count.
    recordInput: () => { if (untrack(options.foreground)) lastInputAt = options.now(); }
  };
}
