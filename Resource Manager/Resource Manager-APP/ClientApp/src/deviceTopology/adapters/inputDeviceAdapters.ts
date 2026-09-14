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

export const keyboardAdapter: DeviceAdapter<KeyboardDeviceModel> = {
  id: "keyboard",
  matches: ({ port }) => port.hid?.hidType.includes("键盘") === true
    || port.pnpClass?.toLocaleLowerCase() === "keyboard"
    || port.service?.toLocaleLowerCase() === "kbdhid"
    || hasUsbInterface(port, /HID Boot Keyboard|\[03\/01\/01\]/i),
  createModel: (context) => {
    const title = deviceTitle(context, "USB 键盘");
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context);
    const protocols = interfaceProtocols(context.port);
    const revision = deviceRevision(context.port);
    const pollingInterval = formatPollingInterval(context.port.hid?.inputPollingIntervalMicroseconds);
    const reportRate = formatRate(context.port.hid?.theoreticalReportRateHz, "Hz");
    const scanRate = context.port.hid?.reportedScanRateHz
      ? formatRate(context.port.hid.reportedScanRateHz, "Hz")
      : "标准 HID 未报告";
    const inputProtocol = context.port.hid?.hidType ?? displayValue(context.port.protocol, "HID 键盘");
    const useInternalSummary = context.scope === "internal" && !hasInputTimingEvidence(context.port);
    return {
      adapterId: "keyboard",
      kind: "keyboard",
      deviceTypeLabel: "键盘",
      title,
      subtitle: useInternalSummary
        ? [inputProtocol, facts?.transport].filter(Boolean).join(" · ")
        : `HID 键盘 · ${connection.currentLink}`,
      badge: "键盘",
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
          ["输入协议", inputProtocol],
          ["USB 轮询周期", pollingInterval],
          ["理论报告率", reportRate],
          ["内部扫描率", scanRate]
        ),
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: ["键盘", "keyboard", "hid boot keyboard", ...protocols]
    };
  }
};

export const mouseAdapter: DeviceAdapter<MouseDeviceModel> = {
  id: "mouse",
  matches: ({ port }) => port.hid?.hidType.includes("鼠标") === true
    || port.pnpClass?.toLocaleLowerCase() === "mouse"
    || port.service?.toLocaleLowerCase() === "mouhid"
    || hasUsbInterface(port, /HID Boot Mouse|\[03\/01\/02\]/i),
  createModel: (context) => {
    const title = deviceTitle(context, "USB 鼠标");
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context);
    const protocols = interfaceProtocols(context.port);
    const revision = deviceRevision(context.port);
    const pollingInterval = formatPollingInterval(context.port.hid?.inputPollingIntervalMicroseconds);
    const reportRate = formatRate(context.port.hid?.theoreticalReportRateHz, "Hz");
    const dpi = context.port.hid?.reportedDpi
      ? `${context.port.hid.reportedDpi.toLocaleString()} DPI`
      : "标准 HID 未报告";
    const inputProtocol = context.port.hid?.hidType ?? displayValue(context.port.protocol, "HID 鼠标");
    const useInternalSummary = context.scope === "internal" && !hasInputTimingEvidence(context.port);
    return {
      adapterId: "mouse",
      kind: "mouse",
      deviceTypeLabel: "鼠标",
      title,
      subtitle: useInternalSummary
        ? [inputProtocol, facts?.transport].filter(Boolean).join(" · ")
        : `HID 鼠标 · ${connection.currentLink}`,
      badge: "鼠标",
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
          ["输入协议", inputProtocol],
          ["USB 轮询周期", pollingInterval],
          ["理论报告率", reportRate],
          ["DPI", dpi]
        ),
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: ["鼠标", "mouse", "hid boot mouse", ...protocols]
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
    ["输入协议", inputProtocol],
    ["内部传输", facts?.transport],
    ["驱动服务", facts?.driverService],
    ["设备状态", facts?.deviceStatus]
  );
}
