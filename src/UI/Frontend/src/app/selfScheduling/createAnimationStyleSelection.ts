import { createComputed, createMemo, createSignal, untrack, type Accessor } from "solid-js";
import { animationStyleMoves } from "../../presentation/animationStyle.ts";
import type { AnimationStyleId } from "../../presentation/animationStyle.ts";
import type { ResourceManagerSelfGpuGrade } from "../../types.ts";

/** “最近有操作”的时长：前端在前台、这么久之内在前端有过指针、键盘或滚轮操作，就是活跃（docs/animation_style_2026-10-26.md）。 */
export const AUTO_ANIMATION_RECENT_INPUT_MS = 5_000;
/** 自调度结果“需要节省”时，按自调度自动关闭动画选的风格。 */
export const AUTO_ANIMATION_OPTIMIZE_STYLE: AnimationStyleId = "none";

/**
 * 实际生效的动画风格。开关「按自调度自动关闭动画」打开、选中的风格会动时，在选中的风格和 none 之间选；
 * 只在前端回到前台的那一刻、且不活跃时按当时的自调度结果重新选（需要节省就选 none），在前台期间、在后台、活跃时都不改。
 * 开关关上，或选中的是 none、ultra 时，选中的风格直接生效。系统的减弱动效设置不读。
 */
export function createAnimationStyleSelection(options: {
  style: Accessor<AnimationStyleId>;
  autoDisable: Accessor<boolean>;
  foreground: Accessor<boolean>;
  gpuGrade: Accessor<ResourceManagerSelfGpuGrade>;
  now: () => number;
}) {
  // 上一次重新选的结果：要不要关掉动画。开始时不关，选中的风格生效。
  const [disabled, setDisabled] = createSignal(false);
  let wasForeground = false;
  let lastInputAt = Number.NEGATIVE_INFINITY;

  createComputed(() => {
    const applies = options.autoDisable() && animationStyleMoves(options.style());
    const foreground = options.foreground();
    const returnedToForeground = foreground && !wasForeground;
    wasForeground = foreground;
    if (!applies) {
      setDisabled(false);
    } else if (returnedToForeground && options.now() - lastInputAt >= AUTO_ANIMATION_RECENT_INPUT_MS) {
      setDisabled(untrack(options.gpuGrade) === "optimize");
    }
  });

  return {
    effectiveStyle: createMemo<AnimationStyleId>(() => (disabled() ? AUTO_ANIMATION_OPTIMIZE_STYLE : options.style())),
    // Activity is input while already in the foreground. Input that arrives before the foreground
    // signal, such as the click that activates the window, belongs to the return and does not count.
    recordInput: () => { if (untrack(options.foreground)) lastInputAt = options.now(); }
  };
}
