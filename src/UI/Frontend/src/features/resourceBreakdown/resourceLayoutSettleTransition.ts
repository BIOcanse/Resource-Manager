import type { TaskRun, TaskScope } from "../../frontendRuntime/task/TaskScope.ts";
import { defineTaskKey } from "../../frontendRuntime/task/TaskDescriptor.ts";
import type { ResourceSegmentLayout } from "./resourceSegmentLayout.ts";
import { animationStyleMoves } from "../../presentation/animationStyle.ts";
import type { AnimationStyleId } from "../../presentation/animationStyle.ts";
import {
  waitForTaskDelay,
  type TaskDelay
} from "../../frontendRuntime/task/waitForTaskDelay.ts";

// 分段过渡走 --motion-bar（随动画风格变），分隔线等它走完再多等这一段余量才回来。
export const resourceLayoutSettleMarginMs = 80;
export const resourceLayoutSettleTaskKey = defineTaskKey<void>(
  "resource-breakdown.layout-settle");

type Layout = readonly ResourceSegmentLayout<{ softwareId: string; value: number }>[];

export function shouldRunResourceLayoutSettleTransition(
  layoutChanged: boolean,
  previousStyle: AnimationStyleId,
  nextStyle: AnimationStyleId
): boolean {
  return layoutChanged || (animationStyleMoves(previousStyle) && !animationStyleMoves(nextStyle));
}

export function createResourceLayoutSettleController(
  scope: TaskScope,
  setMoving: (moving: boolean) => void,
  delay: TaskDelay = waitForTaskDelay
): (
  layout: Layout,
  animationStyle: AnimationStyleId,
  readBarDurationMs: () => number
) => TaskRun<void> | null {
  let initialized = false;
  let previousLayout: Layout = [];
  let previousStyle: AnimationStyleId = "desktop";

  return (layout, animationStyle, readBarDurationMs) => {
    if (!initialized) {
      initialized = true;
      previousLayout = layout;
      previousStyle = animationStyle;
      return null;
    }

    const shouldRun = shouldRunResourceLayoutSettleTransition(
      layout.length !== previousLayout.length || layout.some((item, index) => {
        const previous = previousLayout[index];
        return !previous || item.segment.softwareId !== previous.segment.softwareId
          || item.left !== previous.left || item.right !== previous.right;
      }),
      previousStyle,
      animationStyle);
    previousLayout = layout;
    previousStyle = animationStyle;
    const moves = animationStyleMoves(animationStyle);
    return shouldRun
      ? startResourceLayoutSettleTransition(
          scope,
          moves,
          moves ? readBarDurationMs() : 0,
          setMoving,
          delay)
      : null;
  };
}

export function startResourceLayoutSettleTransition(
  scope: TaskScope,
  animated: boolean,
  barDurationMs: number,
  setMoving: (moving: boolean) => void,
  delay: TaskDelay = waitForTaskDelay
): TaskRun<void> {
  return scope.run({
    key: resourceLayoutSettleTaskKey,
    durability: "transient",
    visibility: "hidden",
    concurrency: "replace"
  }, async (context) => {
    if (!animated) {
      context.commit(() => setMoving(false));
      return;
    }

    if (!context.commit(() => setMoving(true))) {
      return;
    }
    await delay(context.signal, barDurationMs + resourceLayoutSettleMarginMs);
    context.commit(() => setMoving(false));
  });
}
