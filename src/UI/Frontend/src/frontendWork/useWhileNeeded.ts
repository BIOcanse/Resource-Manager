import { useFrontendWork } from "./FrontendWorkContext.tsx";
import type { FrontendWorkId } from "./frontendWorkIds.ts";
import { bindWhileNeeded, type WhileNeededEnter } from "./whileNeededBinding.ts";

/** 分区里用：需求来自本分区的工作 id（可见区域控制器判定，已经包含窗口可见）。 */
export function useWhileNeeded(workId: FrontendWorkId, enter: WhileNeededEnter): void {
  bindWhileNeeded(useFrontendWork(), workId, enter);
}
