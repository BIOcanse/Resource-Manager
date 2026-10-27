import { createEffect, onCleanup, untrack } from "solid-js";
import type { FrontendWorkController } from "./useFrontendWorkController.ts";
import type { FrontendWorkId } from "./frontendWorkIds.ts";

/**
 * 进入：登记这次活动要用的东西（计时器、全局监听、首次读取），同步、短，不建 DOM、不等待。
 * signal 只属于这一次进入，退出时 abort：进入里带等待的异步工作（首次读取）用它取消，
 * 取消后剩下的部分不写状态、不发布结果，迟到的结果落不到下一次进入上。
 * 返回的退出撤销这次进入取得的全部东西；没有要撤销的就不返回。
 */
export type WhileNeededEnter = (signal: AbortSignal) => (() => void) | void;

/**
 * 分区的活动（docs/frontend_runtime_foundation_2026-08-22.md 第 4.4 节“建造与活动”）：
 * isNeeded(workId) 变真时进入，变假或所在的 owner 卸下时退出。再次进入和第一次进入做同样的事。
 * 退出不是删除：DOM 和已经接受的当前值都还在。
 */
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
