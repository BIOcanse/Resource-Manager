import { renderBackendMessage } from "../presentation/backendMessage.ts";
import { uiText } from "../text.ts";
import type { DeviceTopologyDisplayConnection, DeviceTopologyPort } from "../types.ts";

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
