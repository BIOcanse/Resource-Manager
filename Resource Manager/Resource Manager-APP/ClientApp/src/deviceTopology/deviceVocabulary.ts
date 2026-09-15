import { uiText } from "../text.ts";

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
