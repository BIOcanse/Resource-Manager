import { renderBackendMessage } from "../../../presentation/backendMessage.ts";
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
  matches: ({ port }) => port.display?.active === true
    && port.display.targetAvailable === true,
  createModel: (context) => {
    const display = context.port.display!;
    const title = deviceTitle(context, uiText.deviceAdapters.externalMonitor);
    const resolution = displayValue(display.resolution);
    const refreshRate = displayValue(display.refreshRate);
    const connectorTechnology = displayValue(display.connectorTechnology, context.port.protocol);
    const hdrState = resolveHdrState(display);
    const bitDepth = display.bitsPerColorChannel
      ? uiText.deviceAdapters.bitsPerChannel(String(display.bitsPerColorChannel))
      : "--";
    const colorEncoding = displayValue(display.colorEncoding);
    const colorSpace = displayValue(display.colorSpace);
    const displayTechnology = renderBackendMessage(display.displayTechnology, "--");
    const panelTechnology = displayValue(display.panelTechnology, uiText.deviceAdapters.edidNotReported);
    const sdrWhiteLevel = display.sdrWhiteLevelNits
      ? `${display.sdrWhiteLevelNits.toLocaleString()} nits`
      : "--";
    const peakLuminance = display.maximumLuminanceNits
      ? `${display.maximumLuminanceNits.toLocaleString()} nits`
      : "--";
    const fullFrameLuminance = display.maximumFullFrameLuminanceNits
      ? `${display.maximumFullFrameLuminanceNits.toLocaleString()} nits`
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
      displayState: uiText.deviceAdapters.activeDisplayTarget,
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
