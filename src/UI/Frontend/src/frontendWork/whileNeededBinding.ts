import { createEffect, onCleanup, untrack } from "solid-js";
import type { FrontendWorkController } from "./useFrontendWorkController.ts";
import type { FrontendWorkId } from "./frontendWorkIds.ts";

/** Enter synchronously without DOM work or waits. Abort async work on exit, suppress late results, and return cleanup for all acquired resources. */
export type WhileNeededEnter = (signal: AbortSignal) => (() => void) | void;

/** Enter while work is needed; exit on demand loss or owner disposal. Reentry repeats setup while DOM and accepted values remain intact. */
export function bindWhileNeeded(
  work: Pick<FrontendWorkController, "isNeeded">,
  workId: FrontendWorkId,
  enter: WhileNeededEnter
): void {
  createEffect(() => {
    if (!work.isNeeded(workId)) return;
    const entry = new AbortController();
    const exit = untrack(() => enter(entry.signal));
    onCleanup(() => {
      entry.abort();
      if (exit) untrack(exit);
    });
  });
}
