import type {
  OperationCommandDescriptor
} from "../../frontendRuntime/operations/OperationRegistry.ts";
import { uiText } from "../../text.ts";

export function componentInstallCommand(
  id: string,
  acknowledgeExternalTerms: boolean,
  versionChoice?: string | null
): OperationCommandDescriptor {
  return {
    key: `component.install:${id}`,
    url: `/api/components/${encodeURIComponent(id)}/install`,
    body: { acknowledgeExternalTerms, versionChoice: versionChoice ?? null },
    fallbackError: uiText.misc.installStartFailed
  };
}

export function diskUsageScanCommand(
  request: { scope: string; mode: string; target: string }
): OperationCommandDescriptor {
  return {
    // A fixed key lets the coordinator deduplicate disk scans so only one runs at a time.
    key: "disk-usage.scan",
    url: "/api/disk-usage/scan",
    body: request,
    fallbackError: uiText.diskUsage.scan
  };
}

export function softwareUninstallCommand(
  id: string,
  confirmOperation: boolean
): OperationCommandDescriptor {
  return {
    key: `software.uninstall:${id}`,
    url: "/api/software/uninstall",
    body: { id, confirmOperation },
    fallbackError: uiText.misc.uninstallStartFailed
  };
}

