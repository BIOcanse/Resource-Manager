import { useFrontendWork } from "./FrontendWorkContext.tsx";
import type { FrontendWorkId } from "./frontendWorkIds.ts";
import { bindWhileNeeded, type WhileNeededEnter } from "./whileNeededBinding.ts";

/** Partition demand includes window visibility and is keyed by the partition work ID. */
export function useWhileNeeded(workId: FrontendWorkId, enter: WhileNeededEnter): void {
  bindWhileNeeded(useFrontendWork(), workId, enter);
}
