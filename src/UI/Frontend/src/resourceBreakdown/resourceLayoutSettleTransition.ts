import type { TaskRun, TaskScope } from "../frontendRuntime/task/TaskScope.ts";
import { defineTaskKey } from "../frontendRuntime/task/TaskDescriptor.ts";
import type { ResourceSegmentLayout } from "./resourceSegmentLayout.ts";
import {
  waitForTaskDelay,
  type TaskDelay
} from "../frontendRuntime/task/waitForTaskDelay.ts";

export const resourceLayoutSettleDurationMs = 440;
export const resourceLayoutSettleTaskKey = defineTaskKey<void>(
  "resource-breakdown.layout-settle");

export type ResourceLayoutAnimationMode = "normal" | "none" | "ultra";
type Layout = readonly ResourceSegmentLayout<{ softwareId: string; value: number }>[];

export function shouldRunResourceLayoutSettleTransition(
  layoutChanged: boolean,
  previousMode: ResourceLayoutAnimationMode,
  nextMode: ResourceLayoutAnimationMode
): boolean {
  return layoutChanged || (previousMode === "normal" && nextMode !== "normal");
}

export function createResourceLayoutSettleController(
  scope: TaskScope,
  setMoving: (moving: boolean) => void,
  delay: TaskDelay = waitForTaskDelay
): (
  layout: Layout,
  animationMode: ResourceLayoutAnimationMode
) => TaskRun<void> | null {
  let initialized = false;
  let previousLayout: Layout = [];
  let previousMode: ResourceLayoutAnimationMode = "normal";

  return (layout, animationMode) => {
    if (!initialized) {
      initialized = true;
      previousLayout = layout;
      previousMode = animationMode;
      return null;
    }

    const shouldRun = shouldRunResourceLayoutSettleTransition(
      layout.length !== previousLayout.length || layout.some((item, index) => {
        const previous = previousLayout[index];
        return !previous || item.segment.softwareId !== previous.segment.softwareId
          || item.left !== previous.left || item.right !== previous.right;
      }),
      previousMode,
      animationMode);
    previousLayout = layout;
    previousMode = animationMode;
    return shouldRun
      ? startResourceLayoutSettleTransition(
          scope,
          animationMode === "normal",
          setMoving,
          delay)
      : null;
  };
}

export function startResourceLayoutSettleTransition(
  scope: TaskScope,
  animated: boolean,
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
    await delay(context.signal, resourceLayoutSettleDurationMs);
    context.commit(() => setMoving(false));
  });
}
