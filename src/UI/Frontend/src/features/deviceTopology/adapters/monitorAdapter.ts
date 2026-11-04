import { formatInterfaceNumber } from "../../../presentation/interfaceNumbers.ts";
import { renderBackendMessage } from "../../../presentation/backendMessage.ts";
import { portProtocol } from "../deviceVocabulary.ts";
import { connectedDisplay } from "../deviceTopologyDisplay.ts";
import {
  connectionFacts,
  deviceTitle,
  displayValue,
  joinSummary,
  summaryFields
} from "./adapterEvidence.ts";
import type { DeviceAdapter, MonitorDeviceModel } from "./types";
import type { DeviceTopologyDisplayConnection } from "../../../types";
import { uiText } from "../../../text.ts";

export const monitorAdapter: DeviceAdapter<MonitorDeviceModel> = {
  id: "monitor",
  matches: ({ port }) => connectedDisplay(port) !== null,
  createModel: (context) => {
    const display = context.port.display!;
    const title = deviceTitle(context, uiText.deviceAdapters.externalMonitor);
    const resolution = displayValue(display.resolution);
    const refreshRate = displayValue(display.refreshRate);
    const connectorTechnology = displayValue(display.connectorTechnology, portProtocol(context.port));
    const hdrState = resolveHdrState(display);
    const bitDepth = display.bitsPerColorChannel
      ? uiText.deviceAdapters.bitsPerChannel(String(display.bitsPerColorChannel))
      : "--";
    const colorEncoding = displayValue(display.colorEncoding);
    const colorSpace = displayValue(display.colorSpace);
    const displayTechnology = renderBackendMessage(display.displayTechnology, "--");
    const panelTechnology = displayValue(display.panelTechnology, uiText.deviceAdapters.edidNotReported);
    const sdrWhiteLevel = display.sdrWhiteLevelNits
      ? `${formatInterfaceNumber(display.sdrWhiteLevelNits)} nits`
      : "--";
    const peakLuminance = display.maximumLuminanceNits
      ? `${formatInterfaceNumber(display.maximumLuminanceNits)} nits`
      : "--";
    const fullFrameLuminance = display.maximumFullFrameLuminanceNits
      ? `${formatInterfaceNumber(display.maximumFullFrameLuminanceNits)} nits`
      : "--";
    const physicalSize = displayValue(display.physicalSize);
    const connection = connectionFacts(context);
    const deviceType = display.internal ? uiText.deviceAdapters.internalDisplayPanel : uiText.deviceAdapters.monitor;
    return {
      adapterId: "monitor",
      kind: "monitor",
      deviceTypeLabel: deviceType,
      title,
      subtitle: joinSummary(resolution, refreshRate, hdrState === "--" ? undefined : hdrState),
      badge: display.internal ? uiText.deviceAdapters.internalPanelBadge : uiText.deviceAdapters.monitor,
      iconKind: "monitor",
      ...connection,
      resolution,
      refreshRate,
      connectorTechnology,
      displayState: display.active
        ? uiText.deviceAdapters.activeDisplayTarget
        : uiText.deviceAdapters.inactiveDisplayTarget,
      hdrState,
      bitDepth,
      colorEncoding,
      colorSpace,
      displayTechnology,
      panelTechnology,
      sdrWhiteLevel,
      peakLuminance,
      fullFrameLuminance,
      physicalSize,
      summaryFields: summaryFields(
        [uiText.deviceAdapters.label.currentMode, joinSummary(resolution, refreshRate)],
        [uiText.deviceAdapters.label.hdrAdvancedColor, hdrState],
        [uiText.deviceAdapters.label.outputBitDepth, bitDepth],
        [uiText.deviceAdapters.label.displayTechnology, display.panelTechnology ?? displayTechnology]
      ),
      capabilityLabels: [display.hdrFormats, display.edidVersion, display.physicalSize]
        .filter((value): value is string => Boolean(value?.trim())),
      searchTerms: [uiText.deviceAdapters.monitor, "monitor", resolution, refreshRate, connectorTechnology, hdrState, bitDepth]
    };
  }
};

function resolveHdrState(display: DeviceTopologyDisplayConnection) {
  const format = display.hdrFormats?.trim();
  if (display.advancedColorEnabled === true) {
    return joinSummary(format, uiText.deviceAdapters.hdrOn);
  }
  if (display.advancedColorSupported === true) {
    return joinSummary(format ?? uiText.deviceAdapters.advancedColorSupported, uiText.deviceAdapters.hdrCurrentlyOff);
  }
  if (display.advancedColorSupported === false) {
    return format ? uiText.deviceAdapters.hdrFormatUnavailable(format) : uiText.deviceAdapters.advancedColorUnsupported;
  }
  return format ?? "--";
}
