import {
  edidDigitalInterfaceLabel,
  outputTechnologyLabel
} from "../features/deviceTopology/deviceVocabulary.ts";
import { uiText } from "../text.ts";
import type { BackendMessage } from "../types.ts";

/** Render backend domain/code/fact arguments in the current language. Unknown codes use generic copy and log the identifiers; factual parameters remain unchanged. */
export const backendMessageDomains = {
  dependency: 1,
  gpuPlacement: 2,
  metric: 3,
  software: 4,
  deviceTopology: 5,
  operation: 6,
  kernelLeak: 7
} as const;

type Renderer = (args: readonly string[]) => string;

function dependencyRenderers(): Record<number, Renderer> {
  const copy = uiText.backendMessage.dependency;
  return {
    1: () => copy.managedRootRemovable,
    2: () => copy.managedRootEmpty,
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
    22: () => copy.installerLaunched,
    23: () => copy.componentFilesExtracted,
    24: () => copy.runtimeFilesUnavailable,
    25: () => copy.payloadMissing,
    26: () => copy.updateAvailable
  };
}

/** Suppress settled dependency states already expressed by buttons. Keep actionable installer, validation, and bridge messages. */
export function isSettledDependencyMessage(
  message: BackendMessage | null | undefined
): boolean {
  if (!message || message.domain !== backendMessageDomains.dependency) {
    return false;
  }
  // Settled managed-install, download, and reused-install states.
  // Settled provider and built-in validation states.
  return [5, 7, 8, 10, 11, 16, 18].includes(message.code);
}

function operationRenderers(): Record<number, Renderer> {
  const copy = uiText.managementFeedback;
  return {
    1: () => copy.networkFailure,
    2: () => copy.fileUnavailable,
    3: () => copy.accessDenied,
    4: () => copy.requestRejected,
    5: () => copy.ioFailure,
    6: () => copy.unexpectedFailure,
    7: () => copy.noExecutionEvidence,
    8: () => copy.outcomeUncertain
  };
}

function softwareRenderers(): Record<number, Renderer> {
  return {
    13: () => uiText.managementFeedback.managedRootRemoved,
    14: () => uiText.managementFeedback.uninstallerLaunchRequested
  };
}

function metricRenderers(): Record<number, Renderer> {
  const copy = uiText.backendMessage.metric;
  return {
    1: () => copy.notExposed,
    2: (args) => copy.needsComponent(args[0] ?? ""),
    3: () => copy.noValidReading,
    4: () => copy.notApplicable
  };
}

function gpuPlacementRenderers(): Record<number, Renderer> {
  const copy = uiText.backendMessage.gpuPlacement;
  return {
    1: () => copy.singleAdapter
  };
}

/** Kernel-leak action unavailability and risk messages; no arguments. */
function kernelLeakRenderers(): Record<number, Renderer> {
  const copy = uiText.backendMessage.kernelLeak;
  return {
    1: () => copy.actionNotProvided,
    2: () => copy.restartHolderRisk,
    3: () => copy.forceReleaseHandlesRisk
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
    // Code 64 carries a prejoined device-name chain with no translatable wording.
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

/** Raw domain/code and factual arguments for tooltips and unknown messages. */
export function backendMessageCodeText(message: BackendMessage): string {
  const args = message.args ?? [];
  return `${message.domain}/${message.code}${args.length > 0 ? ` [${args.join(", ")}]` : ""}`;
}

export function renderBackendMessage(
  message: BackendMessage | null | undefined,
  fallback?: string
): string {
  if (!message) {
    return fallback ?? "";
  }

  // Resolve each domain table in the current language at call time.
  const renderersByDomain: Record<number, () => Record<number, Renderer>> = {
    [backendMessageDomains.dependency]: dependencyRenderers,
    [backendMessageDomains.gpuPlacement]: gpuPlacementRenderers,
    [backendMessageDomains.metric]: metricRenderers,
    [backendMessageDomains.software]: softwareRenderers,
    [backendMessageDomains.deviceTopology]: deviceTopologyRenderers,
    [backendMessageDomains.operation]: operationRenderers,
    [backendMessageDomains.kernelLeak]: kernelLeakRenderers
  };
  const renderer = renderersByDomain[message.domain]?.()[message.code];
  if (!renderer) {
    console.warn(
      `[backend-message] 未知消息：域 ${message.domain} 码 ${message.code}`);
    return fallback ?? uiText.backendMessage.unknown;
  }

  return renderer(message.args ?? []);
}
