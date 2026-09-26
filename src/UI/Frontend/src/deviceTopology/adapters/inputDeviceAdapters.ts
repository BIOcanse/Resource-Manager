import { hidTypeLabel } from "../deviceVocabulary.ts";
import {
  capabilityLabels,
  connectionFacts,
  deviceRevision,
  deviceTitle,
  displayValue,
  formatPollingInterval,
  formatRate,
  hasUsbInterface,
  internalDeviceFacts,
  interfaceProtocols,
  summaryFields
} from "./adapterEvidence.ts";
import type { DeviceAdapter, KeyboardDeviceModel, MouseDeviceModel } from "./types";
import { uiText } from "../../text.ts";

export const keyboardAdapter: DeviceAdapter<KeyboardDeviceModel> = {
  id: "keyboard",
  matches: ({ port }) => port.hid?.hidType === "keyboard"
    || port.pnpClass?.toLocaleLowerCase() === "keyboard"
    || port.service?.toLocaleLowerCase() === "kbdhid"
    || hasUsbInterface(port, /HID Boot Keyboard|\[03\/01\/01\]/i),
  createModel: (context) => {
    const title = deviceTitle(context, uiText.deviceAdapters.usbKeyboard);
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context);
    const protocols = interfaceProtocols(context.port);
    const revision = deviceRevision(context.port);
    const pollingInterval = formatPollingInterval(context.port.hid?.inputPollingIntervalMicroseconds);
    const reportRate = formatRate(context.port.hid?.theoreticalReportRateHz, "Hz");
    const scanRate = context.port.hid?.reportedScanRateHz
      ? formatRate(context.port.hid.reportedScanRateHz, "Hz")
      : uiText.deviceAdapters.standardHidNotReported;
    const inputProtocol = hidTypeLabel(context.port.hid?.hidType)
      ?? displayValue(context.port.protocol, uiText.deviceAdapters.hidKeyboard);
    const useInternalSummary = context.scope === "internal" && !hasInputTimingEvidence(context.port);
    return {
      adapterId: "keyboard",
      kind: "keyboard",
      deviceTypeLabel: uiText.deviceAdapters.keyboard,
      title,
      subtitle: useInternalSummary
        ? [inputProtocol, facts?.transport].filter(Boolean).join(" · ")
        : uiText.deviceAdapters.hidKeyboardLink(connection.currentLink),
      badge: uiText.deviceAdapters.keyboard,
      iconKind: "keyboard",
      ...connection,
      hidMode: inputProtocol,
      deviceRevision: revision,
      interfaceProtocols: protocols,
      pollingInterval,
      reportRate,
      scanRate,
      summaryFields: useInternalSummary
        ? internalInputSummary(inputProtocol, facts)
        : summaryFields(
          [uiText.deviceAdapters.label.inputProtocol, inputProtocol],
          [uiText.deviceAdapters.label.usbPollingInterval, pollingInterval],
          [uiText.deviceAdapters.label.theoreticalReportRate, reportRate],
          [uiText.deviceAdapters.label.internalScanRate, scanRate]
        ),
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: [uiText.deviceAdapters.keyboard, "keyboard", "hid boot keyboard", ...protocols]
    };
  }
};

export const mouseAdapter: DeviceAdapter<MouseDeviceModel> = {
  id: "mouse",
  matches: ({ port }) => port.hid?.hidType === "mouse"
    || port.pnpClass?.toLocaleLowerCase() === "mouse"
    || port.service?.toLocaleLowerCase() === "mouhid"
    || hasUsbInterface(port, /HID Boot Mouse|\[03\/01\/02\]/i),
  createModel: (context) => {
    const title = deviceTitle(context, uiText.deviceAdapters.usbMouse);
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context);
    const protocols = interfaceProtocols(context.port);
    const revision = deviceRevision(context.port);
    const pollingInterval = formatPollingInterval(context.port.hid?.inputPollingIntervalMicroseconds);
    const reportRate = formatRate(context.port.hid?.theoreticalReportRateHz, "Hz");
    const dpi = context.port.hid?.reportedDpi
      ? `${context.port.hid.reportedDpi.toLocaleString()} DPI`
      : uiText.deviceAdapters.standardHidNotReported;
    const inputProtocol = hidTypeLabel(context.port.hid?.hidType)
      ?? displayValue(context.port.protocol, uiText.deviceAdapters.hidMouse);
    const useInternalSummary = context.scope === "internal" && !hasInputTimingEvidence(context.port);
    return {
      adapterId: "mouse",
      kind: "mouse",
      deviceTypeLabel: uiText.deviceAdapters.mouse,
      title,
      subtitle: useInternalSummary
        ? [inputProtocol, facts?.transport].filter(Boolean).join(" · ")
        : uiText.deviceAdapters.hidMouseLink(connection.currentLink),
      badge: uiText.deviceAdapters.mouse,
      iconKind: "mouse",
      ...connection,
      hidMode: inputProtocol,
      deviceRevision: revision,
      interfaceProtocols: protocols,
      pollingInterval,
      reportRate,
      dpi,
      summaryFields: useInternalSummary
        ? internalInputSummary(inputProtocol, facts)
        : summaryFields(
          [uiText.deviceAdapters.label.inputProtocol, inputProtocol],
          [uiText.deviceAdapters.label.usbPollingInterval, pollingInterval],
          [uiText.deviceAdapters.label.theoreticalReportRate, reportRate],
          ["DPI", dpi]
        ),
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: [uiText.deviceAdapters.mouse, "mouse", "hid boot mouse", ...protocols]
    };
  }
};

function hasInputTimingEvidence(port: import("../../types").DeviceTopologyPort) {
  return port.usb != null
    || port.hid?.inputPollingIntervalMicroseconds != null
    || port.hid?.theoreticalReportRateHz != null;
}

function internalInputSummary(
  inputProtocol: string,
  facts: ReturnType<typeof internalDeviceFacts>
) {
  return summaryFields(
    [uiText.deviceAdapters.label.inputProtocol, inputProtocol],
    [uiText.deviceAdapters.label.internalTransport, facts?.transport],
    [uiText.deviceAdapters.label.driverService, facts?.driverService],
    [uiText.deviceAdapters.label.deviceStatus, facts?.deviceStatus]
  );
}
