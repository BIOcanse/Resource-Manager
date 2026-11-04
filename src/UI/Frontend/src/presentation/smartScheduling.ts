import type { HostManagerAppliedPlacementReceipt } from "../types.ts";
import { uiText } from "../text.ts";

export function smartPlacementTarget(
  placement: Pick<HostManagerAppliedPlacementReceipt, "processName" | "processId">,
  softwareName?: string
): string {
  const processName = placement.processName || "";
  const name = softwareName && processName && softwareName.toLocaleLowerCase() !== processName.toLocaleLowerCase()
    ? `${softwareName} / ${processName}` : processName || softwareName || uiText.smartReport.fallbackTarget;
  return placement.processId === null ? name : uiText.smartReport.processTarget(name, placement.processId);
}
