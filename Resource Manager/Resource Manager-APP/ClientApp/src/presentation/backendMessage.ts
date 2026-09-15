import { uiText } from "../text.ts";
import type { BackendMessage } from "../types.ts";

/**
 * 后端只说「哪一类、哪一条、带哪些事实」，这里按当前语言把它渲染成一句话。
 *
 * - 域与码的定义在后端 `Domain/Messages/BackendMessageCodes.cs`，两边靠契约测试保持一致。
 * - 认不出的域或码显示通用兜底，并把域/码写进控制台日志，不把数字给用户看。
 * - 参数只包含事实（名字、路径、版本），措辞全部来自这里的文案包。
 */
export const backendMessageDomains = {
  dependency: 1,
  gpuPlacement: 2,
  metric: 3
} as const;

type Renderer = (args: readonly string[]) => string;

function dependencyRenderers(): Record<number, Renderer> {
  const copy = uiText.backendMessage.dependency;
  return {
    1: () => copy.managedRootRemovable,
    2: () => copy.managedRootEmpty,
    3: () => copy.categoryTag,
    4: () => copy.uninstallAction,
    5: () => copy.installedInManagedRoot,
    6: () => copy.installerCached,
    7: () => copy.downloadableWithVersionChoice,
    8: () => copy.downloadable,
    9: () => copy.manualAcquisition,
    10: (args) => copy.reusingExternalInstall(args[0] ?? ""),
    11: () => copy.providerVerified,
    12: () => copy.providerRuntimeUnverified,
    13: () => copy.componentFilesUnverified,
    14: () => copy.runtimeAvailableBridgePending,
    15: () => copy.providerBridgeMissing,
    16: () => copy.bundledVerified,
    17: () => copy.bundledUnverified,
    18: () => copy.alreadyInstalledReuse,
    19: () => copy.installerDownloaded,
    20: (args) => copy.installerDownloadedVersion(args[0] ?? ""),
    21: () => copy.sharedRuntimeInstalled,
    22: () => copy.installerLaunched
  };
}

function gpuPlacementRenderers(): Record<number, Renderer> {
  const copy = uiText.backendMessage.gpuPlacement;
  return {
    1: () => copy.singleAdapter
  };
}

export function renderBackendMessage(
  message: BackendMessage | null | undefined,
  fallback?: string
): string {
  if (!message) {
    return fallback ?? "";
  }

  const renderers = message.domain === backendMessageDomains.dependency
    ? dependencyRenderers()
    : message.domain === backendMessageDomains.gpuPlacement
      ? gpuPlacementRenderers()
      : null;
  const renderer = renderers?.[message.code];
  if (!renderer) {
    console.warn(
      `[backend-message] 未知消息：域 ${message.domain} 码 ${message.code}`);
    return fallback ?? uiText.backendMessage.unknown;
  }

  return renderer(message.args ?? []);
}
