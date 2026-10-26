import { displayConnectorTechnology, portHardwareKind, portProtocol, userFacingNetworkState } from "./deviceVocabulary.ts";
import { deviceTopologySummaryFields } from "./deviceTopologyPresentation.ts";
import type { ExternalInterfaceNodeRole } from "./externalInterfaceTree.ts";
import type { InternalInterfaceNodeRole } from "./internalInterfaceTree.ts";
import type { SpecializedDeviceModel } from "./adapters/adapterRegistry.ts";
import type { DeviceTopologyDisplayConnection, DeviceTopologyPort, DeviceTopologyUsbConnection } from "../../types.ts";
import {
  compactUserDetailSections,
  userDetailItem,
  userDetailSection,
  type UserDetailItem
} from "../../presentation/userDetails.ts";
import { renderBackendMessage } from "../../presentation/backendMessage.ts";
import { uiText } from "../../text.ts";

/*
 * 设备拓扑侧栏摘要和「详细信息」弹窗的分节内容：只由选中的节点、它的父节点和直属子节点数决定，
 * 不读别的状态。视图（DeviceTopologyView.tsx）只负责渲染。
 */

export type DeviceTopologyScope = "external" | "internal";
export type DeviceTopologyNodeRole = ExternalInterfaceNodeRole | InternalInterfaceNodeRole;

export interface DeviceTopologyListNode {
  id: string;
  port: DeviceTopologyPort;
  title: string;
  subtitle: string;
  badge: string;
  connectorKind: string;
  iconKind?: string;
  connectionState?: "connected" | "disconnected" | "unknown";
  depth: number;
  role?: DeviceTopologyNodeRole;
  parentId?: string;
  path?: string[];
  specializedDevice?: SpecializedDeviceModel;
}

/**
 * 详情头名称下面的一句概括：设备写设备类型，接口写外部／内部接口，连接状态明确时跟在后面。
 * 速率、描述这类完整内容只在下面对应的格子里，不在这里和清单副标题里再写一遍。
 */
export function nodeHeadline(node: DeviceTopologyListNode, scope: DeviceTopologyScope): string {
  const category = isDeviceRole(node.role)
    ? node.specializedDevice?.deviceTypeLabel ?? uiText.deviceTopology.connectedDevice
    : scope === "external" ? uiText.deviceTopology.externalScope : uiText.deviceTopology.internalScope;
  return node.connectionState === "connected" || node.connectionState === "disconnected"
    ? `${category} · ${connectionStateLabel(node.connectionState)}`
    : category;
}

/** 侧栏「关键信息」：接口按接口摘要，设备优先用专用适配器的摘要。 */
export function nodeSummaryFields(node: DeviceTopologyListNode, childCount: number, scope: DeviceTopologyScope) {
  if (isInterfaceRole(node.role)) {
    return interfaceSummaryFields(node, childCount, scope);
  }
  return node.specializedDevice?.summaryFields ?? deviceTopologySummaryFields(node.port);
}

/**
 * 端口上接着的显示器；没有时为 null。
 *
 * 只有数据明确说明端口上什么都没接（未激活、目标不可用，也没有显示器名称、设备路径或 EDID）才算没有；
 * 显示器接着但未激活时仍然有，注册表里的 EDID 声明照常显示。
 */
export function attachedDisplay(port: DeviceTopologyPort | null | undefined): DeviceTopologyDisplayConnection | null {
  const display = port?.display;
  if (!display) {
    return null;
  }
  const attached = display.active
    || display.targetAvailable
    || Boolean(display.monitorName?.trim())
    || display.monitorDevicePath != null
    || display.edidRawDeclaredBase64 != null
    || display.modeCapabilityDeclared != null;
  return attached ? display : null;
}

export function deviceUserDetailSections(
  port: DeviceTopologyPort,
  node: DeviceTopologyListNode,
  parent: DeviceTopologyListNode | null,
  scope: DeviceTopologyScope,
  childCount: number,
  summary: readonly { label: string; value: string }[])
{
  const isDevice = isDeviceRole(node.role);
  const isInterface = isInterfaceRole(node.role);
  const display = attachedDisplay(port);
  const network = port.network;
  const usb = port.usb;
  // 概览先列和侧栏相同的关键信息；概览、连接两节里同一名称只出现一次，先出现的为准。
  const shown = new Map<string, string>();
  const firstShown = (item: UserDetailItem | null) => {
    if (!item || shown.has(item.label)) {
      return null;
    }
    shown.set(item.label, item.value);
    return item;
  };
  // 后面各节里与前面同名同值的行是重复，不再列出；同名不同值的照常列出。
  const unlessShownSame = (item: UserDetailItem | null) =>
    item && shown.get(item.label) === item.value ? null : item;
  // 专用摘要已经写过同一个值的通用行（「上游接口」与「连接到」、「当前链路」与「当前连接速率」、
  // 「输入协议 键盘」与「设备类型 键盘」）是重复，不再列出。
  const summaryValues = new Set(summary.filter((field) => isReportedDeviceValue(field.value)).map((field) => field.value));
  const unlessSummarized = (item: UserDetailItem | null) =>
    item && summaryValues.has(item.value) ? null : item;

  return compactUserDetailSections([
    userDetailSection(uiText.deviceTopology.section.overview, [
      ...summary
        .filter((field) => isReportedDeviceValue(field.value))
        .map((field) => firstShown(userDetailItem(field.label, field.value))),
      firstShown(userDetailItem(uiText.deviceTopology.label.category, isDevice ? uiText.deviceTopology.connectedDevice : scope === "external" ? uiText.deviceTopology.externalScope : uiText.deviceTopology.internalScope)),
      firstShown(unlessSummarized(userDetailItem(isDevice ? uiText.deviceTopology.label.deviceType : uiText.deviceTopology.label.interfaceType, isDevice
        ? node.specializedDevice?.deviceTypeLabel ?? hardwareKindLabel(port)
        : connectorLabel(port.connectorKind)))),
      firstShown(userDetailItem(uiText.deviceTopology.label.connectionState, connectionStateLabel(node.connectionState))),
      parent ? firstShown(unlessSummarized(userDetailItem(uiText.deviceTopology.label.connectedTo, parent.title))) : null,
      isInterface ? firstShown(userDetailItem(uiText.deviceTopology.label.directChildren, uiText.deviceTopology.countSuffix(childCount))) : null
    ]),
    userDetailSection(uiText.deviceTopology.section.connection, [
      firstShown(userDetailItem(uiText.deviceTopology.label.bus, reportedDeviceValue(busLabel(port.busKind)))),
      firstShown(unlessSummarized(userDetailItem(isDevice ? uiText.deviceTopology.label.currentDeviceSpeed : uiText.deviceTopology.label.currentInterfaceSpeed, reportedDeviceValue(port.speed)))),
      firstShown(userDetailItem(uiText.deviceTopology.label.maxSpeed, reportedDeviceValue(port.physicalMaximumSpeed))),
      firstShown(userDetailItem(uiText.deviceTopology.label.protocol, reportedDeviceValue(isInterface ? interfaceProtocol(node, scope) : portProtocol(port)))),
      firstShown(userDetailItem(uiText.deviceTopology.label.manufacturer, reportedDeviceValue(port.manufacturer))),
      port.advancedInterconnect
        ? firstShown(userDetailItem(uiText.deviceTopology.label.interconnectTechnology, reportedDeviceValue(port.advancedInterconnect.technology)))
        : null
    ]),
    display ? userDetailSection(uiText.deviceTopology.section.display, [
      userDetailItem(uiText.deviceTopology.label.deviceName, reportedDeviceValue(display.monitorName)),
      userDetailItem(uiText.deviceTopology.label.activeResolution, reportedDeviceValue(display.resolution)),
      userDetailItem(uiText.deviceTopology.label.refreshRate, reportedDeviceValue(display.refreshRate)),
      userDetailItem(uiText.deviceTopology.label.connectionLocation, display.internal ? uiText.deviceTopology.displayLocation.internal : uiText.deviceTopology.displayLocation.external)
    ]) : null,
    network ? userDetailSection(uiText.deviceTopology.section.network, [
      userDetailItem(uiText.deviceTopology.label.networkInterface, reportedDeviceValue(network.interfaceName)),
      unlessShownSame(userDetailItem(uiText.deviceTopology.label.connectionState, userFacingNetworkState(network.connectionState))),
      userDetailItem(uiText.deviceTopology.label.transmitSpeed, reportedDeviceValue(network.transmitLinkSpeed)),
      userDetailItem(uiText.deviceTopology.label.receiveSpeed, reportedDeviceValue(network.receiveLinkSpeed)),
      network.activeMtuBytes ? userDetailItem("MTU", `${network.activeMtuBytes} B`) : null,
      userDetailItem(uiText.deviceTopology.label.macAddress, reportedDeviceValue(network.permanentAddress))
    ]) : null,
    usb ? userDetailSection("USB", [
      userDetailItem(uiText.deviceTopology.label.port, formatUsbPort(usb)),
      userDetailItem(uiText.deviceTopology.label.usbConnection, renderBackendMessage(usb.connectionStatus)),
      userDetailItem(uiText.deviceTopology.label.connector, formatUsbConnector(usb)),
      userDetailItem(uiText.deviceTopology.label.supportedProtocols, reportedDeviceValue(usb.supportedProtocols)),
      userDetailItem(uiText.deviceTopology.label.portCapability, reportedDeviceValue(formatUsbCapability(usb))),
      userDetailItem(uiText.deviceTopology.label.deviceSpecification, reportedDeviceValue(usb.deviceSpecification)),
      userDetailItem(uiText.deviceTopology.label.deviceRevision, reportedDeviceValue(usb.deviceRevision)),
      userDetailItem(uiText.deviceTopology.label.usbClass, usb.deviceClassCode === null ? uiText.deviceTopology.machine.unknown : String(usb.deviceClassCode)),
      userDetailItem("VID / PID", reportedDeviceValue(formatUsbVidPid(usb))),
      userDetailItem(uiText.deviceTopology.label.serialNumber, reportedDeviceValue(usb.serialNumber))
    ]) : null,
    isDevice ? userDetailSection(uiText.deviceTopology.section.deviceIdentity, [
      userDetailItem(uiText.deviceTopology.label.hardwareIdentity, reportedDeviceValue(port.deviceId))
    ]) : null
  ]);
}

export function isDeviceRole(role?: DeviceTopologyNodeRole) {
  return role === "external-hub"
    || role === "attached-device"
    || role === "internal-controller"
    || role === "internal-device"
    || role === "internal-function";
}

export function isInterfaceRole(role?: DeviceTopologyNodeRole) {
  return role === "host-interface"
    || role === "downstream-interface"
    || role === "internal-interface";
}

function reportedDeviceValue(value?: string | null) {
  return isReportedDeviceValue(value) ? String(value).trim() : "";
}

function isReportedDeviceValue(value?: string | null) {
  const text = String(value ?? "").trim();
  return Boolean(text && text !== "--" && text.toLocaleLowerCase() !== "unknown" && !text.includes(uiText.deviceTopology.notReportedMarker));
}

// 高级互联节点的「硬件类别」就是它的互联角色，措辞在角色码里。
function hardwareKindLabel(port: DeviceTopologyPort) {
  return port.advancedInterconnect
    ? renderBackendMessage(port.advancedInterconnect.role, portHardwareKind(port))
    : portHardwareKind(port);
}

function interfaceProtocol(node: DeviceTopologyListNode, scope: DeviceTopologyScope) {
  if (scope === "internal") {
    switch (node.connectorKind) {
      case "pcie": return "PCI Express";
      case "usb-internal": return "USB";
      case "internal-display": return node.port.display
        ? displayConnectorTechnology(node.port.display)
        : uiText.deviceTopology.internalDisplay;
      case "audio": return "HD Audio";
      case "bluetooth": return "Bluetooth";
      case "acpi": return "ACPI";
      case "storage": return node.port.storage?.busType ?? portProtocol(node.port);
      default: return portProtocol(node.port);
    }
  }
  return node.port.usb?.supportedProtocols
    ?? node.port.display?.connectorTechnology
    ?? portProtocol(node.port);
}

function interfaceSummaryFields(node: DeviceTopologyListNode, childCount: number, scope: DeviceTopologyScope) {
  if (scope === "external") {
    return deviceTopologySummaryFields(node.port);
  }
  return [
    { label: uiText.deviceTopology.label.interfaceType, value: node.title },
    { label: uiText.deviceTopology.label.connectionState, value: connectionStateLabel(node.connectionState) },
    { label: uiText.deviceTopology.label.directChildren, value: uiText.deviceTopology.countSuffix(childCount) },
    { label: uiText.deviceTopology.label.protocol, value: interfaceProtocol(node, scope) }
  ];
}

function connectionStateLabel(state?: "connected" | "disconnected" | "unknown") {
  if (state === "connected") return uiText.deviceTopology.connectionState.connected;
  if (state === "disconnected") return uiText.deviceTopology.connectionState.disconnected;
  return uiText.deviceTopology.connectionState.unknown;
}

// portNumber 是这个设备（或接口）在上级集线器上的端口号，不是第几个集线器。
function formatUsbPort(usb: DeviceTopologyUsbConnection) {
  return uiText.deviceTopology.usbHubPort(usb.portNumber);
}

function formatUsbVidPid(usb: DeviceTopologyUsbConnection) {
  if (!usb.vendorId && !usb.productId) {
    return "--";
  }

  return `${usb.vendorId ?? "VID --"} / ${usb.productId ?? "PID --"}`;
}

function formatUsbCapability(usb: DeviceTopologyUsbConnection) {
  if (usb.operatingAtSuperSpeedPlusOrHigher) {
    return uiText.deviceTopology.usbCapability.currentSuperSpeedPlus;
  }

  if (usb.operatingAtSuperSpeedOrHigher) {
    return uiText.deviceTopology.usbCapability.currentSuperSpeed;
  }

  if (usb.superSpeedPlusCapableOrHigher) {
    return uiText.deviceTopology.usbCapability.supportsSuperSpeedPlus;
  }

  if (usb.superSpeedCapableOrHigher) {
    return uiText.deviceTopology.usbCapability.supportsSuperSpeed;
  }

  if (usb.superSpeedCapableOrHigher === false || usb.superSpeedPlusCapableOrHigher === false) {
    return uiText.deviceTopology.usbCapability.notReported;
  }

  return "--";
}

function formatUsbConnector(usb: DeviceTopologyUsbConnection) {
  const connector = usb.portConnectorIsTypeC ? "USB-C" : "USB";
  if (usb.portIsUserConnectable === true) {
    return uiText.deviceTopology.connectorSuffix.userPluggable(connector);
  }

  if (usb.portIsUserConnectable === false) {
    return uiText.deviceTopology.connectorSuffix.internal(connector);
  }

  return usb.portConnectorIsTypeC ? connector : "--";
}

function connectorLabel(kind: string) {
  switch (kind) {
    case "usb-a":
      return uiText.deviceTopology.usbGenericConnector;
    case "usb-c":
      return "USB-C / Type-C";
    case "thunderbolt":
      return "Thunderbolt / USB4";
    case "hdmi":
      return "HDMI";
    case "displayport":
      return "DisplayPort";
    case "mini-displayport":
      return "Mini DisplayPort";
    case "dvi":
      return "DVI";
    case "vga":
      return "VGA / HD15";
    case "internal-display":
      return uiText.deviceTopology.deviceCategory.internalDisplayPanel;
    case "wireless-display":
      return uiText.deviceTopology.deviceCategory.wirelessDisplay;
    case "rj45":
      return "RJ45 / Ethernet";
    case "audio":
      return uiText.deviceTopology.deviceCategory.audio;
    case "sd-card":
      return uiText.deviceTopology.deviceCategory.cardReader;
    case "pcie":
      return "PCIe";
    case "bluetooth":
      return "Bluetooth";
    default:
      return uiText.deviceTopology.deviceCategory.generic;
  }
}

function busLabel(kind: string) {
  switch (kind) {
    case "usb":
      return "USB";
    case "usb4":
      return "USB4";
    case "thunderbolt":
      return "Thunderbolt";
    case "pci":
      return "PCI / PCIe";
    case "display":
      return uiText.deviceTopology.deviceCategory.display;
    case "network":
      return uiText.deviceTopology.deviceCategory.network;
    case "audio":
      return uiText.deviceTopology.deviceCategory.audio;
    case "storage":
      return uiText.deviceTopology.deviceCategory.storage;
    case "bluetooth":
      return uiText.deviceTopology.deviceCategory.bluetooth;
    case "system":
      return uiText.deviceTopology.deviceCategory.systemFirmware;
    default:
      return uiText.deviceTopology.deviceCategory.unknown;
  }
}
