import type {
  OperationCommandDescriptor
} from "../../frontendRuntime/operations/OperationRegistry.ts";

export function componentInstallCommand(
  id: string,
  acknowledgeExternalTerms: boolean,
  versionChoice?: string | null
): OperationCommandDescriptor {
  return {
    key: `component.install:${id}`,
    url: `/api/components/${encodeURIComponent(id)}/install`,
    body: { acknowledgeExternalTerms, versionChoice: versionChoice ?? null },
    fallbackError: "安装操作启动失败"
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
    fallbackError: "卸载操作启动失败"
  };
}

export function migrationExecuteCommand(
  request: unknown
): OperationCommandDescriptor {
  return {
    key: "migration.execute",
    url: "/api/migrations/execute",
    body: request,
    fallbackError: "迁移操作启动失败"
  };
}

export function migrationRestoreCommand(
  id: string,
  confirmExecution: boolean
): OperationCommandDescriptor {
  return {
    key: `migration.restore:${id}`,
    url: "/api/migrations/restore",
    body: { id, confirmExecution },
    fallbackError: "恢复操作启动失败"
  };
}

export function migrationDiscoveryStartCommand(
  request: unknown
): OperationCommandDescriptor {
  return {
    key: "discovery.start",
    url: "/api/migrations/discovery/start",
    body: request,
    fallbackError: "发现操作启动失败"
  };
}
