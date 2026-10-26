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

/**
 * 端口的名字：设备自报的优先，没有就渲染我们自己生成的那条码。
 * 两者恰好有一个非空，所以这里不需要再兜底。
 */
export function portDisplayName(port: DeviceTopologyPort) {
  return port.displayName ?? renderBackendMessage(port.displayNameCode);
}

/** 端口的硬件类别：技术名优先，没有就渲染生成的那条码；两者都没有时返回空串。 */
export function portHardwareKind(port: DeviceTopologyPort) {
  return port.hardwareKind ?? renderBackendMessage(port.hardwareKindCode, "");
}

/**
 * 端口的协议：后端给的多是技术名（USB、HID、Wi-Fi、HDMI 2.1 等），原样显示；
 * 其中几个普通英文词（Ethernet、Audio、Display）按当前语言出；显示连接给的是 Windows 输出技术 id 时按输出技术出。
 */
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

/** 显示接口的名称：机型接口档案给的接口名优先，否则用 Windows 输出技术。 */
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

/**
 * 后端在设备拓扑里给的是取值 id（见后端 `Domain/DeviceTopology/DeviceTopologySnapshot.cs`
 * 里的 DeviceHidTypes / DevicePortableDeviceTypes / DeviceStorageHealthStates /
 * DeviceStorageMountStates），措辞全部在这里按当前语言取。
 *
 * 认不出的 id 返回 undefined，由调用方决定回落成什么；不把 id 直接显示给用户。
 */
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

// 后端给的是状态 id（见 DeviceNetworkConnectionStates），措辞在这里。
export function userFacingNetworkState(value?: string | null) {
  switch (String(value ?? "").trim()) {
    case "connected": return uiText.deviceTopology.connectionState.connected;
    case "disconnected": return uiText.deviceTopology.connectionState.disconnected;
    case "not-reported": return uiText.deviceTopology.notReportedMarker;
    default: return uiText.deviceTopology.connectionState.unknown;
  }
}
