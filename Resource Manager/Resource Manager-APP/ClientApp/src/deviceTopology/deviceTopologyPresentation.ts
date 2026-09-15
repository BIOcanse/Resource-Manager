import type { DeviceTopologyPort } from "../types";
import { uiText } from "../text.ts";

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
      badge: isControllerNode(port) ? uiText.deviceInterface.controllerBadge : port.protocol
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
      { label: uiText.deviceTopology.label.interfaceType, value: presentation.title },
      { label: uiText.deviceTopology.label.connectionState, value: presentation.connectionLabel ?? uiText.deviceTopology.connectionState.unknown },
      { label: uiText.deviceTopology.label.protocol, value: externalInterfaceProtocol(port) }
    ];
    if (port.display?.connectorInstance && port.display.connectorInstance > 0) {
      fields.push({ label: uiText.deviceInterface.interfaceIndex, value: String(port.display.connectorInstance) });
    } else if (!port.display) {
      fields.push({ label: uiText.deviceTopology.label.currentInterfaceSpeed, value: displayValue(port.speed) });
    }
    return fields;
  }

  if (isControllerNode(port)) {
    return [
      { label: uiText.deviceSpecialized.controllerType, value: port.advancedInterconnect ? uiText.deviceInterface.systemInterconnectNode : displayValue(port.hardwareKind) },
      { label: uiText.deviceTopology.label.deviceStatus, value: displayValue(port.status) },
      { label: uiText.deviceTopology.label.driverService, value: displayValue(port.service) },
      { label: uiText.deviceTopology.label.protocol, value: displayValue(port.protocol) }
    ];
  }

  return [
    { label: uiText.deviceTopology.label.deviceType, value: displayValue(port.hardwareKind) },
    { label: uiText.deviceTopology.label.deviceStatus, value: displayValue(port.status) },
    { label: uiText.deviceTopology.label.bus, value: busLabel(port.busKind) },
    { label: uiText.deviceTopology.label.protocol, value: displayValue(port.protocol) }
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
    const state = port.network.connectionState.trim();
    if (state === "disconnected") {
      return "disconnected";
    }
    if (state === "connected") {
      return "connected";
    }
  }

  // 走到这里说明这个端口没有 USB / 显示 / 网络三种连接事实中的任何一种。
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
      return uiText.deviceTopology.connectionState.connected;
    case "disconnected":
      return uiText.deviceTopology.connectionState.disconnected;
    default:
      return uiText.deviceTopology.connectionState.unknown;
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
      return uiText.deviceInterface.connector.usbA;
    case "usb-c":
      return uiText.deviceInterface.connector.usbC;
    case "thunderbolt":
      return uiText.deviceInterface.connector.thunderbolt;
    case "hdmi":
      return uiText.deviceInterface.connector.hdmi;
    case "displayport":
      return uiText.deviceInterface.connector.displayPort;
    case "mini-displayport":
      return uiText.deviceInterface.connector.miniDisplayPort;
    case "dvi":
      return uiText.deviceInterface.connector.dvi;
    case "vga":
      return uiText.deviceInterface.connector.vga;
    case "rj45":
      return uiText.deviceInterface.connector.rj45;
    case "audio":
      return uiText.deviceInterface.connector.audio;
    case "power":
      return uiText.deviceInterface.connector.power;
    case "sd-card":
      return uiText.deviceInterface.connector.sdCard;
    default:
      return uiText.deviceInterface.connector.generic;
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
      return uiText.deviceInterface.category.displayOutput;
    case "network":
      return uiText.deviceInterface.category.network;
    case "audio":
      return uiText.deviceInterface.category.audio;
    case "storage":
      return uiText.deviceInterface.category.storage;
    case "bluetooth":
      return uiText.deviceInterface.category.bluetooth;
    case "system":
      return uiText.deviceInterface.category.system;
    default:
      return displayValue(busKind);
  }
}

function displayValue(value: string | null | undefined) {
  const normalized = value?.trim();
  return normalized || "--";
}

// 后端读不出速率时给的是 null，占位词不再从字符串里认。
function meaningfulSpeed(value: string | null) {
  const normalized = (value ?? "").trim();
  return normalized ? normalized : undefined;
}

function firstText(...values: Array<string | null | undefined>) {
  return values.map((value) => value?.trim()).find(Boolean);
}

function joinSummary(...values: Array<string | null | undefined>) {
  return values.map((value) => value?.trim()).filter(Boolean).join(" · ");
}
