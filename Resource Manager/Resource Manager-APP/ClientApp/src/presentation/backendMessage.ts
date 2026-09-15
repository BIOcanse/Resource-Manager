import {
  edidDigitalInterfaceLabel,
  outputTechnologyLabel
} from "../deviceTopology/deviceVocabulary.ts";
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
  metric: 3,
  software: 4,
  deviceTopology: 5
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

function softwareRenderers(): Record<number, Renderer> {
  const copy = uiText.backendMessage.software;
  return {
    1: () => copy.hasUninstallEntry,
    2: () => copy.missingUninstallEntry,
    3: () => copy.uninstallAction,
    4: () => copy.cannotUninstallAction,
    5: () => copy.windowsUninstallerDescription,
    6: () => copy.noUninstallUnknownRoot,
    7: () => copy.manualClassificationNoUninstall,
    8: () => copy.controlledNoUninstall,
    9: () => copy.selfNoUninstall,
    10: () => copy.adaptedNoUninstall,
    11: () => copy.legacyControlledNoUninstall,
    12: () => copy.portableNoUninstall
  };
}

function metricRenderers(): Record<number, Renderer> {
  const copy = uiText.backendMessage.metric;
  return {
    1: () => copy.notExposed,
    2: (args) => copy.needsComponent(args[0] ?? ""),
    3: () => copy.noValidReading
  };
}

function gpuPlacementRenderers(): Record<number, Renderer> {
  const copy = uiText.backendMessage.gpuPlacement;
  return {
    1: () => copy.singleAdapter
  };
}

function deviceTopologyRenderers(): Record<number, Renderer> {
  const copy = uiText.backendMessage.deviceTopology;
  return {
    1: () => copy.enumerationOnly,
    2: () => copy.noVisibleNodes,
    3: (args) => copy.conflictingFacts(args[0] ?? ""),
    4: () => copy.incompleteBrandModel,
    5: () => copy.usbChainUnavailable,
    6: (args) => copy.pnpEnumerationFailed(args[0] ?? ""),
    7: (args) => copy.nativeDevicePropertiesFailed(args[0] ?? ""),
    8: (args) => copy.usbHubIoctlFailed(args[0] ?? ""),
    9: (args) => copy.networkAdapterPropertiesFailed(args[0] ?? ""),
    10: (args) => copy.displayCoordinatorNotReady(args[0] ?? ""),
    11: (args) => copy.displayCoordinatorReadFailed(args[0] ?? ""),
    12: (args) => copy.storageCapabilitiesIncomplete(args[0] ?? ""),
    13: (args) => copy.usbControllerEnumerationFailed(args[0] ?? ""),
    14: (args) => copy.usbHubEnumerationFailed(args[0] ?? ""),
    15: (args) => copy.usbControllerRelationshipFailed(args[0] ?? ""),
    16: () => copy.confidenceUsbHubIoctl,
    17: () => copy.confidenceWmiChainDeviceManager,
    18: () => copy.confidenceWmiChainDeviceManagerNameInference,
    19: () => copy.confidenceWmiChain,
    20: () => copy.confidenceWmiChainNameInference,
    21: () => copy.confidenceDeviceManager,
    22: () => copy.confidenceDeviceManagerNameInference,
    23: () => copy.confidenceNameInference,
    24: () => copy.confidenceDeviceEnumeration,
    25: () => copy.confidenceNetAdapter,
    26: () => copy.confidencePnpServiceRole,
    27: () => copy.confidenceUsbConnectorProperties,
    28: () => copy.confidenceActiveDisplayPath,
    29: () => copy.confidenceOemProfileDisplayTarget,
    30: () => copy.sourceUsbIoctlWmiSetupApiPnp,
    31: () => copy.sourceUsbIoctlSetupApiPnp,
    32: () => copy.sourceWmiSetupApiPnp,
    33: () => copy.sourceWmiPnp,
    34: () => copy.sourceSetupApiPnp,
    35: () => copy.sourcePnpEnumeration,
    36: () => copy.sourceNetAdapterSetupApiPnp,
    37: () => copy.sourcePnpServiceSetupApi,
    38: () => copy.sourceUsbHubIoctl,
    39: () => copy.sourceQueryDisplayConfig,
    40: () => copy.sourceOemProfileQueryDisplayConfig,
    41: (args) => copy.interconnectEvidencePnpService(args[0] ?? ""),
    42: () => copy.roleUcsiConnectorManager,
    43: () => copy.roleUsb4HostRouter,
    44: () => copy.roleUsb4DeviceRouter,
    45: () => copy.roleUsb4P2PNetwork,
    46: () => copy.usbNotConnected,
    47: () => copy.usbConnected,
    48: () => copy.usbEnumerationFailed,
    49: () => copy.usbDeviceGeneralFailure,
    50: () => copy.usbDeviceCausedOvercurrent,
    51: () => copy.usbInsufficientPower,
    52: () => copy.usbInsufficientBandwidth,
    53: () => copy.usbHubNestedTooDeep,
    54: () => copy.usbDeviceInLegacyHub,
    55: () => copy.usbEnumerating,
    56: () => copy.usbResetting,
    57: (args) => copy.usbUnknownStatus(args[0] ?? ""),
    58: () => copy.sourceWin32DiskAssociations,
    59: () => copy.sourceMsftDiskAssociations,
    60: () => copy.sourceUsbHidEndpointDescriptors,
    61: () => copy.sourceUsbVideoClassDescriptors,
    62: () => copy.sourceUsbDescriptorsAndWindows,
    63: () => copy.sourceWindowsWpdPnp,
    // 64 的参数就是拼好的设备链，链上全是设备名，没有可翻译的措辞。
    64: (args) => args[0] ?? "",
    65: () => copy.pathPnpEnumeration,
    66: () => copy.pathDeviceManagerProperties,
    67: (args) => copy.pathUsbHubPort(args[0] ?? ""),
    68: (args) => copy.pathWindowsDisplayPath(
      outputTechnologyLabel(args[0]),
      args[1] ?? ""),
    69: (args) => copy.pathOemProfile(args[0] ?? ""),
    70: (args) => copy.nameConnectedDeviceOnConnector(args[0] ?? ""),
    71: (args) => copy.nameIdleConnector(args[0] ?? ""),
    72: () => copy.nameInternalDisplayPanel,
    73: (args) => copy.nameActiveMonitor(outputTechnologyLabel(args[0])),
    74: (args) => copy.nameConnectorInterface(args[0] ?? ""),
    75: (args) => copy.kindPhysicalConnector(args[0] ?? ""),
    76: (args) => copy.kindActiveDisplayPath(outputTechnologyLabel(args[0])),
    77: () => copy.displayTechnologyAnalog,
    78: (args) => copy.displayTechnologyDigital(edidDigitalInterfaceLabel(args[0]))
  };
}

export function renderBackendMessage(
  message: BackendMessage | null | undefined,
  fallback?: string
): string {
  if (!message) {
    return fallback ?? "";
  }

  // 每个域一张表；表在这里按当前语言现取，所以切语言后不用重建任何缓存。
  const renderersByDomain: Record<number, () => Record<number, Renderer>> = {
    [backendMessageDomains.dependency]: dependencyRenderers,
    [backendMessageDomains.gpuPlacement]: gpuPlacementRenderers,
    [backendMessageDomains.metric]: metricRenderers,
    [backendMessageDomains.software]: softwareRenderers,
    [backendMessageDomains.deviceTopology]: deviceTopologyRenderers
  };
  const renderer = renderersByDomain[message.domain]?.()[message.code];
  if (!renderer) {
    console.warn(
      `[backend-message] 未知消息：域 ${message.domain} 码 ${message.code}`);
    return fallback ?? uiText.backendMessage.unknown;
  }

  return renderer(message.args ?? []);
}
