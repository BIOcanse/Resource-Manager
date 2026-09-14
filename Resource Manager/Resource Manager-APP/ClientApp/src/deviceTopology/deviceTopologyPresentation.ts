import type { DeviceTopologyPort } from "../types";

export type DeviceTopologyConnectionState = "connected" | "disconnected" | "unknown";

export interface DeviceTopologyNodePresentation {
  title: string;
  subtitle: string;
  badge: string;
  connectionState?: DeviceTopologyConnectionState;
  connectionLabel?: string;
  connectionTarget?: string;
}

export interface DeviceTopologySummaryField {
  label: string;
  value: string;
}

const externalConnectorKinds = new Set([
  "usb-a",
  "usb-c",
  "thunderbolt",
  "hdmi",
  "displayport",
  "mini-displayport",
  "dvi",
  "vga",
  "rj45",
  "audio",
  "power",
  "sd-card"
]);

const controllerServices = new Set([
  "storahci",
  "stornvme",
  "ucmucsiaacpidevice",
  "ucmucsiacpiclient",
  "usb4devicerouter",
  "usb4hostrouter",
  "usbxhci"
]);

export function describeDeviceTopologyNode(port: DeviceTopologyPort): DeviceTopologyNodePresentation {
  if (!isExternalDeviceInterface(port)) {
    return {
      title: port.displayName,
      subtitle: joinSummary(port.hardwareKind, meaningfulSpeed(port.speed)),
      badge: isControllerNode(port) ? "控制器" : port.protocol
    };
  }

  const connectionState = resolveConnectionState(port);
  const connectionLabel = connectionStateLabel(connectionState);
  const connectionTarget = resolveConnectionTarget(port);
  const transport = meaningfulSpeed(port.speed) ?? port.protocol;
  return {
    title: externalInterfaceLabel(port.connectorKind),
    subtitle: joinSummary(connectionLabel, connectionTarget, transport),
    badge: connectionLabel,
    connectionState,
    connectionLabel,
    connectionTarget
  };
}

export function deviceTopologySummaryFields(port: DeviceTopologyPort): DeviceTopologySummaryField[] {
  if (isExternalDeviceInterface(port)) {
    const presentation = describeDeviceTopologyNode(port);
    const fields: DeviceTopologySummaryField[] = [
      { label: "接口类型", value: presentation.title },
      { label: "连接状态", value: presentation.connectionLabel ?? "状态未知" },
      { label: "协议", value: externalInterfaceProtocol(port) }
    ];
    if (port.display?.connectorInstance && port.display.connectorInstance > 0) {
      fields.push({ label: "接口序号", value: String(port.display.connectorInstance) });
    } else if (!port.display) {
      fields.push({ label: "当前接口速率", value: displayValue(port.speed) });
    }
    return fields;
  }

  if (isControllerNode(port)) {
    return [
      { label: "控制器类型", value: port.advancedInterconnect ? "系统互连控制节点" : displayValue(port.hardwareKind) },
      { label: "设备状态", value: displayValue(port.status) },
      { label: "驱动服务", value: displayValue(port.service) },
      { label: "协议", value: displayValue(port.protocol) }
    ];
  }

  return [
    { label: "设备类型", value: displayValue(port.hardwareKind) },
    { label: "设备状态", value: displayValue(port.status) },
    { label: "总线", value: busLabel(port.busKind) },
    { label: "协议", value: displayValue(port.protocol) }
  ];
}

function isExternalDeviceInterface(port: DeviceTopologyPort) {
  return port.usb?.portIsUserConnectable === true
    || (port.isPhysicalConnector && externalConnectorKinds.has(port.connectorKind.toLocaleLowerCase()));
}

function isControllerNode(port: DeviceTopologyPort) {
  if (port.advancedInterconnect) {
    return true;
  }

  const service = port.service?.trim().toLocaleLowerCase();
  if (service && controllerServices.has(service)) {
    return true;
  }

  if (port.pnpClass?.trim().toLocaleLowerCase() === "scsiadapter") {
    return true;
  }

  return [port.displayName, port.hardwareKind]
    .some((value) => /controller|host router|connector manager|控制器|连接器管理器/i.test(value ?? ""));
}

function resolveConnectionState(port: DeviceTopologyPort): DeviceTopologyConnectionState {
  if (port.usb) {
    return port.usb.deviceConnected ? "connected" : "disconnected";
  }

  if (port.display) {
    return port.display.active && port.display.targetAvailable ? "connected" : "disconnected";
  }

  if (port.network) {
    const state = port.network.connectionState.trim().toLocaleLowerCase();
    if (state.includes("disconnected") || state === "down" || state.includes("未连接")) {
      return "disconnected";
    }
    if (state.includes("connected") || state === "up" || state.includes("已连接")) {
      return "connected";
    }
  }

  if (port.speed.includes("未连接")) {
    return "disconnected";
  }

  return "unknown";
}

function resolveConnectionTarget(port: DeviceTopologyPort) {
  if (port.usb?.deviceConnected) {
    return firstText(port.usb.productName, port.displayName);
  }

  if (port.display?.active) {
    return firstText(port.display.monitorName, port.displayName);
  }

  return undefined;
}

function connectionStateLabel(state: DeviceTopologyConnectionState) {
  switch (state) {
    case "connected":
      return "已连接";
    case "disconnected":
      return "未连接";
    default:
      return "状态未知";
  }
}

function externalInterfaceProtocol(port: DeviceTopologyPort) {
  return displayValue(
    port.usb?.supportedProtocols
      ?? port.display?.connectorTechnology
      ?? port.protocol
  );
}

function externalInterfaceLabel(connectorKind: string) {
  switch (connectorKind.toLocaleLowerCase()) {
    case "usb-a":
      return "USB-A 接口";
    case "usb-c":
      return "USB-C 接口";
    case "thunderbolt":
      return "Thunderbolt 接口";
    case "hdmi":
      return "HDMI 接口";
    case "displayport":
      return "DisplayPort 接口";
    case "mini-displayport":
      return "Mini DisplayPort 接口";
    case "dvi":
      return "DVI 接口";
    case "vga":
      return "VGA 接口";
    case "rj45":
      return "RJ45 接口";
    case "audio":
      return "音频接口";
    case "power":
      return "电源接口";
    case "sd-card":
      return "SD 卡接口";
    default:
      return "外部接口";
  }
}

function busLabel(busKind: string) {
  switch (busKind.toLocaleLowerCase()) {
    case "usb":
      return "USB";
    case "usb4":
      return "USB4";
    case "thunderbolt":
      return "Thunderbolt";
    case "pci":
      return "PCI / PCIe";
    case "display":
      return "显示输出";
    case "network":
      return "网络";
    case "audio":
      return "音频";
    case "storage":
      return "存储";
    case "bluetooth":
      return "蓝牙";
    case "system":
      return "系统";
    default:
      return displayValue(busKind);
  }
}

function displayValue(value: string | null | undefined) {
  const normalized = value?.trim();
  return normalized || "--";
}

function meaningfulSpeed(value: string) {
  const normalized = value.trim();
  return !normalized || normalized === "不适用" || normalized === "未知" || normalized === "未连接"
    ? undefined
    : normalized;
}

function firstText(...values: Array<string | null | undefined>) {
  return values.map((value) => value?.trim()).find(Boolean);
}

function joinSummary(...values: Array<string | null | undefined>) {
  return values.map((value) => value?.trim()).filter(Boolean).join(" · ");
}
