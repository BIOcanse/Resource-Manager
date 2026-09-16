import { formatBytes as formatBytesInUnits } from "../../presentation/byteUnits.ts";
import type { DeviceTopologyPort } from "../../types";
import type {
  DeviceAdapterContext,
  SpecializedDeviceSummaryField,
  SpecializedInternalDeviceFacts
} from "./types";
import { uiText } from "../../text.ts";

export function firstText(...values: Array<string | null | undefined>) {
  return values.map((value) => value?.trim()).find(Boolean);
}

export function displayValue(value: string | null | undefined, fallback = "--") {
  return firstText(value) ?? fallback;
}

export function joinSummary(...values: Array<string | null | undefined>) {
  return values.map((value) => value?.trim()).filter(Boolean).join(" · ");
}

export function meaningfulSpeed(port: DeviceTopologyPort) {
  const value = firstText(port.usb?.negotiatedSpeed, port.speed);
  return !value || value === "不适用" || value === "未知" || value === "未连接"
    ? "--"
    : value;
}

export function deviceTitle(context: DeviceAdapterContext, fallback = uiText.deviceAdapters.usbDevice) {
  return firstText(
    context.port.usb?.productName,
    context.port.display?.monitorName,
    context.fallbackTitle,
    context.port.displayName
  ) ?? fallback;
}

export function interfaceProtocols(port: DeviceTopologyPort) {
  return (port.usb?.interfaceProtocols ?? [])
    .map((value) => value.trim())
    .filter(Boolean);
}

export function hasUsbInterface(port: DeviceTopologyPort, pattern: RegExp) {
  return interfaceProtocols(port).some((value) => pattern.test(value));
}

export function hasAnyEvidence(port: DeviceTopologyPort, pattern: RegExp) {
  return [
    port.displayName,
    port.hardwareKind,
    port.pnpClass,
    port.deviceId,
    port.service,
    port.usb?.deviceClass,
    port.usb?.productName,
    port.advancedInterconnect?.kind,
    port.advancedInterconnect?.technology,
    ...port.hardwareIds,
    ...port.compatibleIds,
    ...interfaceProtocols(port)
  ].some((value) => pattern.test(value ?? ""));
}

export function capabilityLabels(port: DeviceTopologyPort) {
  return interfaceProtocols(port).map((value) => value.replace(/\s*\[[^\]]+\]\s*$/, "").trim());
}

export function summaryFields(...fields: Array<[string, string | null | undefined]>): SpecializedDeviceSummaryField[] {
  return fields.map(([label, value]) => ({ label, value: displayValue(value) }));
}

export function connectionFacts(context: DeviceAdapterContext) {
  return {
    upstreamInterface: displayValue(context.parentTitle),
    currentLink: meaningfulSpeed(context.port)
  };
}

export function internalDeviceFacts(
  context: DeviceAdapterContext
): SpecializedInternalDeviceFacts | undefined {
  if (context.scope !== "internal") {
    return undefined;
  }

  const port = context.port;
  return {
    transport: internalTransport(port),
    manufacturer: displayValue(port.manufacturer, port.idResolution?.vendorName ?? "--"),
    driverService: displayValue(port.service),
    deviceStatus: displayValue(port.status),
    location: displayValue(port.locationInfo),
    catalogIdentity: displayValue(port.idResolution?.deviceName)
  };
}

export function usbSpecification(port: DeviceTopologyPort) {
  return displayValue(port.usb?.deviceSpecification);
}

export function deviceRevision(port: DeviceTopologyPort) {
  return displayValue(port.usb?.deviceRevision);
}

export function activeCapabilities(values: Array<[string, boolean]>) {
  return values.filter(([, enabled]) => enabled).map(([label]) => label);
}

/**
 * 设备拓扑里的容量都是磁盘、分区、可移动存储 —— 天生十进制，
 * 所以固定按存储类交给唯一所有者换算，这里不再自己除 1024。
 */
export function formatBytes(value: number | null | undefined, fallback = "--") {
  return formatBytesInUnits(value, "storage", undefined, fallback);
}

export function formatRate(value: number | null | undefined, unit: string, fallback = "--") {
  return typeof value === "number" && Number.isFinite(value)
    ? `${value.toLocaleString(undefined, { maximumFractionDigits: 2 })} ${unit}`
    : fallback;
}

export function formatPollingInterval(value: number | null | undefined) {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return "--";
  }
  return value >= 1000
    ? `${(value / 1000).toLocaleString(undefined, { maximumFractionDigits: 3 })} ms`
    : `${value.toLocaleString(undefined, { maximumFractionDigits: 1 })} us`;
}

function internalTransport(port: DeviceTopologyPort) {
  const deviceId = port.deviceId.toLocaleUpperCase();
  const service = port.service?.toLocaleLowerCase() ?? "";
  const parent = `${port.nativeParentDeviceId ?? ""} ${port.nativeParentDisplayName ?? ""}`;

  if (deviceId.startsWith("HDAUDIO\\")) return "HD Audio";
  if (deviceId.startsWith("BTH") || service.startsWith("bth") || service === "umpass") return "Bluetooth";
  if (service.startsWith("ucmucsi")) return "UCSI / ACPI";
  if (service === "stornvme") return "NVMe / PCI Express";
  if (service === "storahci") return "SATA / AHCI";
  if (port.storage?.busType) return port.storage.busType;
  if (port.display?.internal) return displayValue(port.display.connectorTechnology, uiText.deviceAdapters.internalDisplay);
  if (port.pnpClass?.toLocaleLowerCase() === "display" || deviceId.startsWith("PCI\\")) return "PCI Express";
  if (port.busKind.toLocaleLowerCase() === "network") return displayValue(port.protocol, uiText.deviceAdapters.internalNetworkBus);
  if (port.busKind.toLocaleLowerCase() === "usb" || deviceId.startsWith("USB\\")) return "USB";
  if (deviceId.startsWith("HID\\") && /I2C/i.test(parent)) return "I2C HID";
  if (deviceId.startsWith("ACPI\\")) return "ACPI";
  return displayValue(port.protocol, port.busKind);
}
