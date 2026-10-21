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
    // 同一时刻只允许一个磁盘扫描，键固定，重复提交由协调器自己挡。
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

