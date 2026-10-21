import { uiText } from "../text.ts";
import type { SystemProcessOperationItem, SystemProcessOperationResult } from "../types";

export function systemProcessResultDetail(item: SystemProcessOperationItem) {
  const label = item.path || item.processName || (item.processId ? uiText.softwareActions.processItem(item.processId) : "");
  return item.message === "CriticalProcessProtected"
    ? `${label}: ${uiText.softwareActions.criticalProcessProtected}`
    : label;
}

export function systemProcessResultSummary(result: SystemProcessOperationResult) {
  const succeeded = result.items.filter(item => item.state === "Succeeded").length;
  return {
    complete: succeeded === result.items.length && result.items.length > 0,
    message: uiText.softwareActions.processActionResult(succeeded, result.items.length)
  };
}
