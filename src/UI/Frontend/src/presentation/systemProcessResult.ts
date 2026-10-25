import { uiText } from "../text.ts";
import type { SystemProcessOperationItem, SystemProcessOperationResult } from "../types";

export function systemProcessResultDetail(item: SystemProcessOperationItem) {
  const label = item.path || item.processName || (item.processId ? uiText.softwareActions.processItem(item.processId) : "");
  const copy = uiText.managementFeedback;
  const outcomes = {
    Terminated: copy.processTerminated,
    DumpCreated: copy.processDumpCreated,
    BackendProtected: copy.processBackendProtected,
    CriticalProcessProtected: uiText.softwareActions.criticalProcessProtected,
    IdentityChanged: copy.processIdentityChanged,
    ProcessExited: copy.processExited,
    AccessDenied: copy.accessDenied,
    NativeFailure: copy.processNativeFailure,
    FileFailure: copy.processFileFailure,
    RequestRejected: copy.requestRejected
  };
  return `${label}: ${outcomes[item.outcome]}`;
}

export function systemProcessResultSummary(result: SystemProcessOperationResult) {
  const succeeded = result.items.filter(item => item.state === "Succeeded").length;
  return {
    complete: succeeded === result.items.length && result.items.length > 0,
    message: result.action === "Terminate"
      ? uiText.managementFeedback.processTerminateSummary(succeeded, result.items.length)
      : uiText.managementFeedback.processDumpSummary(succeeded, result.items.length)
  };
}
