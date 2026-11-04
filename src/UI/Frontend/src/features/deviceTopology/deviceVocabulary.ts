import { renderBackendMessage } from "../../presentation/backendMessage.ts";
import { uiText } from "../../text.ts";
import type { DeviceTopologyDisplayConnection, DeviceTopologyPort } from "../../types.ts";

/** Transport is classified by the backend; raw evidence is never reinterpreted here. */
export function transportLabel(transport: DeviceTopologyPort["transport"]): string {
  switch (transport.code) {
    case null: return `${uiText.deviceTopology.deviceCategory.unknown} (${transport.reason})`;
    case "hd_audio": return "HD Audio";
    case "bluetooth": return "Bluetooth";
    case "ucsi": return "UCSI";
    case "nvme": return "NVMe";
    case "ahci": return "AHCI";
    case "i2c_hid": return "I2C HID";
    case "acpi": return "ACPI";
    case "pci": return "PCI";
    case "usb": return "USB";
    case "usb4": return "USB4";
    default: return transport.code;
  }
}

/** Prefer the reported port name, otherwise its generated code; exactly one is nonempty. */
export function portDisplayName(port: DeviceTopologyPort) {
  return port.displayName ?? renderBackendMessage(port.displayNameCode);
}

/** Prefer the technical hardware class, otherwise its generated code; return empty when neither exists. */
export function portHardwareKind(port: DeviceTopologyPort) {
  return port.hardwareKind ?? renderBackendMessage(port.hardwareKindCode, "");
}

/** Preserve technical protocol names, localize common terms, and resolve Windows output-technology identifiers. */
export function portProtocol(port: Pick<DeviceTopologyPort, "protocol" | "display">) {
  if (port.protocol === null) return uiText.deviceTopology.deviceCategory.unknown;
  if (port.display && port.protocol === port.display.outputTechnology) {
    return outputTechnologyLabel(port.protocol);
  }
  switch (port.protocol) {
    case "Ethernet": return uiText.deviceTopology.ethernetProtocol;
    case "Audio": return uiText.deviceTopology.deviceCategory.audio;
    case "Display": return uiText.deviceTopology.deviceCategory.display;
    default: return port.protocol;
  }
}

/** Prefer the model profile's connector name, otherwise Windows output technology. */
export function displayConnectorTechnology(display: DeviceTopologyDisplayConnection) {
  return display.connectorTechnology ?? outputTechnologyLabel(display.outputTechnology);
}

export function outputTechnologyLabel(value?: string | null) {
  const copy = uiText.deviceTopology.outputTechnology;
  return copy[(value ?? "") as keyof typeof copy] ?? copy.unknown;
}

export function edidDigitalInterfaceLabel(value?: string | null) {
  const copy = uiText.deviceTopology.edidDigitalInterface;
  return copy[(value ?? "") as keyof typeof copy] ?? copy.undefined;
}

/** Localize backend value identifiers; unknown identifiers return undefined for caller fallback instead of becoming user text. */
export function hidTypeLabel(value?: string | null) {
  const copy = uiText.deviceTopology.hidType;
  switch (value) {
    case "keyboard": return copy.keyboard;
    case "mouse": return copy.mouse;
    case "input-device": return copy.inputDevice;
    default: return undefined;
  }
}

export function portableDeviceTypeLabel(value?: string | null) {
  const copy = uiText.deviceTopology.portableDeviceType;
  switch (value) {
    case "phone": return copy.phone;
    case "tablet": return copy.tablet;
    case "camera": return copy.camera;
    case "smart-device": return copy.smartDevice;
    default: return undefined;
  }
}

export function storageHealthLabel(value?: string | null) {
  const copy = uiText.deviceTopology.storageHealth;
  switch (value) {
    case "healthy": return copy.healthy;
    case "warning": return copy.warning;
    case "unhealthy": return copy.unhealthy;
    case "unknown": return copy.unknown;
    default: return undefined;
  }
}

export function mountStateLabel(value?: string | null) {
  const copy = uiText.deviceTopology.mountState;
  switch (value) {
    case "mounted": return copy.mounted;
    case "not-ready": return copy.notReady;
    case "unknown": return copy.unknown;
    default: return undefined;
  }
}

// Localize backend network-connection state identifiers here.
export function userFacingNetworkState(value?: string | null) {
  switch (String(value ?? "").trim()) {
    case "connected": return uiText.deviceTopology.connectionState.connected;
    case "disconnected": return uiText.deviceTopology.connectionState.disconnected;
    case "not-reported": return uiText.deviceTopology.notReportedMarker;
    default: return uiText.deviceTopology.connectionState.unknown;
  }
}
