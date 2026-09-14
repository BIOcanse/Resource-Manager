import type { DeviceTopologyPort } from "../types";
import { resolveSpecializedDevice } from "./adapters/adapterRegistry.ts";
import {
  filterDeviceTopologyTree,
  type DeviceTopologyTreeConnectionState,
  type DeviceTopologyTreeNode
} from "./deviceTopologyTree.ts";

export type ExternalInterfaceNodeRole =
  | "host-interface"
  | "external-hub"
  | "downstream-interface"
  | "attached-device";

export type ExternalInterfaceConnectionState = DeviceTopologyTreeConnectionState;

export interface ExternalInterfaceTreeNode extends DeviceTopologyTreeNode<ExternalInterfaceNodeRole> {}

export interface ExternalInterfaceTreeSummary {
  nodes: ExternalInterfaceTreeNode[];
  hostInterfaceCount: number;
  downstreamInterfaceCount: number;
  attachedDeviceCount: number;
}

interface PendingNode extends Omit<ExternalInterfaceTreeNode, "depth" | "path" | "searchText"> {
  children: PendingNode[];
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

export function buildExternalInterfaceTree(ports: readonly DeviceTopologyPort[]): ExternalInterfaceTreeSummary {
  const usbConnectors = deduplicateUsbConnectors(ports.filter(isUserConnectableUsbPort));
  const connectorByPortId = new Map<string, PendingNode>();
  const deviceByPortId = new Map<string, PendingNode>();
  const upstreamPortByDownstreamHubPath = new Map<string, DeviceTopologyPort>();

  for (const port of usbConnectors) {
    const downstreamPath = normalizeHubPath(port.usb?.downstreamHubDevicePath);
    if (downstreamPath && !upstreamPortByDownstreamHubPath.has(downstreamPath)) {
      upstreamPortByDownstreamHubPath.set(downstreamPath, port);
    }
  }

  const connectorPortIds = new Set(usbConnectors.map((port) => port.id));
  for (const port of usbConnectors) {
    const upstreamPort = upstreamPortByDownstreamHubPath.get(normalizeHubPath(port.usb?.hubDevicePath) ?? "");
    const hasVisibleUpstream = upstreamPort !== undefined
      && upstreamPort.id !== port.id
      && connectorPortIds.has(upstreamPort.id);
    const role: ExternalInterfaceNodeRole = hasVisibleUpstream ? "downstream-interface" : "host-interface";
    const connector = createUsbConnectorNode(port, role, hasVisibleUpstream ? deviceNodeId(upstreamPort.id) : undefined);
    connectorByPortId.set(port.id, connector);

    if (port.usb?.deviceConnected) {
      deviceByPortId.set(port.id, createAttachedDeviceNode(port, connector.id));
    }
  }

  for (const connector of connectorByPortId.values()) {
    if (!connector.parentId) {
      continue;
    }

    const parent = deviceByPortId.get(portIdFromDeviceNodeId(connector.parentId));
    if (!parent || createsCycle(parent, connector, connectorByPortId, deviceByPortId)) {
      connector.parentId = undefined;
      connector.role = "host-interface";
      connector.badge = roleLabel("host-interface");
      continue;
    }

    parent.children.push(connector);
  }

  for (const [portId, device] of deviceByPortId) {
    connectorByPortId.get(portId)?.children.push(device);
  }

  const roots = [...connectorByPortId.values()]
    .filter((node) => !node.parentId)
    .concat(ports.filter(isNonUsbExternalPort).map(createNonUsbRootNode))
    .sort(comparePendingNodes);
  for (const root of roots) {
    applySpecializedDevicePresentation(root);
  }
  const flattened: ExternalInterfaceTreeNode[] = [];
  const visited = new Set<string>();
  for (const root of roots) {
    appendNode(root, 0, [], flattened, visited);
  }

  for (const connector of connectorByPortId.values()) {
    if (!visited.has(connector.id)) {
      connector.parentId = undefined;
      connector.role = "host-interface";
      connector.badge = roleLabel("host-interface");
      applySpecializedDevicePresentation(connector);
      appendNode(connector, 0, [], flattened, visited);
    }
  }

  return {
    nodes: flattened,
    hostInterfaceCount: flattened.filter((node) => node.role === "host-interface").length,
    downstreamInterfaceCount: flattened.filter((node) => node.role === "downstream-interface").length,
    attachedDeviceCount: flattened.filter((node) => node.role === "external-hub" || node.role === "attached-device").length
  };
}

export function filterExternalInterfaceTree(
  nodes: readonly ExternalInterfaceTreeNode[],
  query: string
): ExternalInterfaceTreeNode[] {
  return filterDeviceTopologyTree(nodes, query);
}

export function externalInterfaceRoleLabel(role: ExternalInterfaceNodeRole) {
  return roleLabel(role);
}

function deduplicateUsbConnectors(ports: readonly DeviceTopologyPort[]) {
  const byPort = new Map<string, DeviceTopologyPort>();
  for (const port of ports) {
    const key = `${normalizeHubPath(port.usb?.hubDevicePath) ?? port.id}|${port.usb?.portNumber ?? 0}`;
    const current = byPort.get(key);
    if (!current || connectorCandidateRank(port) > connectorCandidateRank(current)) {
      byPort.set(key, port);
    }
  }
  return [...byPort.values()];
}

function connectorCandidateRank(port: DeviceTopologyPort) {
  return (port.usb?.deviceConnected ? 4 : 0)
    + (port.usb?.deviceIsHub ? 2 : 0)
    + (port.usb?.productName ? 1 : 0);
}

function isUserConnectableUsbPort(port: DeviceTopologyPort) {
  return port.usb !== null
    && port.usb !== undefined
    && (port.usb.portIsUserConnectable === true || port.isPhysicalConnector);
}

function isNonUsbExternalPort(port: DeviceTopologyPort) {
  return !port.usb
    && port.isPhysicalConnector
    && externalConnectorKinds.has(port.connectorKind.toLocaleLowerCase());
}

function createUsbConnectorNode(
  port: DeviceTopologyPort,
  role: "host-interface" | "downstream-interface",
  parentId?: string
): PendingNode {
  const state = port.usb?.deviceConnected ? "connected" : "disconnected";
  const portSuffix = role === "downstream-interface" && port.usb?.portNumber
    ? ` ${port.usb.portNumber}`
    : "";
  return {
    id: connectorNodeId(port.id),
    port,
    role,
    parentId,
    title: `${externalInterfaceLabel(port.connectorKind)}${portSuffix}`,
    subtitle: joinSummary(
      role === "downstream-interface" && port.usb?.portNumber ? `下游端口 ${port.usb.portNumber}` : undefined,
      state === "connected" ? "已连接" : "未连接",
      meaningfulSpeed(port.speed)
    ),
    badge: roleLabel(role),
    connectorKind: port.connectorKind,
    connectionState: state,
    children: []
  };
}

function createAttachedDeviceNode(port: DeviceTopologyPort, parentId: string): PendingNode {
  const role: ExternalInterfaceNodeRole = port.usb?.deviceIsHub ? "external-hub" : "attached-device";
  const title = firstText(port.usb?.productName, port.displayName) ?? "USB 设备";
  return {
    id: deviceNodeId(port.id),
    port,
    role,
    parentId,
    title,
    subtitle: joinSummary(port.manufacturer, port.usb?.deviceClass, meaningfulSpeed(port.speed)),
    badge: port.usb?.deviceIsHub && /dock|docking|扩展坞/i.test(title) ? "扩展坞" : roleLabel(role),
    connectorKind: port.usb?.deviceIsHub ? "cable" : port.connectorKind,
    connectionState: "connected",
    children: []
  };
}

function createNonUsbRootNode(port: DeviceTopologyPort): PendingNode {
  const node: PendingNode = {
    id: connectorNodeId(port.id),
    port,
    role: "host-interface",
    title: externalInterfaceLabel(port.connectorKind),
    subtitle: joinSummary(resolveNonUsbStateLabel(port), port.display ? undefined : meaningfulSpeed(port.speed)),
    badge: roleLabel("host-interface"),
    connectorKind: port.connectorKind,
    connectionState: resolveNonUsbConnectionState(port),
    children: []
  };

  if (isActiveExternalDisplay(port)) {
    node.children.push(createAttachedDisplayNode(port, node.id));
  }

  return node;
}

function createAttachedDisplayNode(port: DeviceTopologyPort, parentId: string): PendingNode {
  const display = port.display!;
  return {
    id: deviceNodeId(port.id),
    port,
    role: "attached-device",
    parentId,
    title: firstText(display.monitorName) ?? "外接显示器",
    subtitle: joinSummary(display.resolution, display.refreshRate),
    badge: "显示器",
    connectorKind: port.connectorKind,
    connectionState: "connected",
    children: []
  };
}

function applySpecializedDevicePresentation(node: PendingNode, parentTitle?: string) {
  if (node.role === "external-hub" || node.role === "attached-device") {
    const downstreamInterfaces = node.children.filter((child) => child.role === "downstream-interface");
    const model = resolveSpecializedDevice({
      scope: "external",
      port: node.port,
      fallbackTitle: node.title,
      parentTitle,
      downstreamInterfaceCount: downstreamInterfaces.length,
      connectedDownstreamInterfaceCount: downstreamInterfaces.filter(
        (child) => child.connectionState === "connected"
      ).length
    });
    node.specializedDevice = model;
    node.title = model.title;
    node.subtitle = model.subtitle;
    node.badge = model.badge;
    node.iconKind = model.iconKind;
  }

  for (const child of node.children) {
    applySpecializedDevicePresentation(child, node.title);
  }
}

function isActiveExternalDisplay(port: DeviceTopologyPort) {
  return port.display?.active === true
    && port.display.targetAvailable === true
    && port.display.internal === false;
}

function appendNode(
  node: PendingNode,
  depth: number,
  parentPath: readonly string[],
  output: ExternalInterfaceTreeNode[],
  visited: Set<string>
) {
  if (!visited.add(node.id)) {
    return;
  }

  const path = [...parentPath, node.title];
  output.push({
    id: node.id,
    port: node.port,
    role: node.role,
    parentId: node.parentId,
    depth,
    title: node.title,
    subtitle: node.subtitle,
    badge: node.badge,
    connectorKind: node.connectorKind,
    iconKind: node.iconKind,
    connectionState: node.connectionState,
    path,
    searchText: createSearchText(node, path),
    specializedDevice: node.specializedDevice
  });

  for (const child of [...node.children].sort(comparePendingNodes)) {
    appendNode(child, depth + 1, path, output, visited);
  }
}

function createSearchText(node: PendingNode, path: readonly string[]) {
  const port = node.port;
  const isDevice = node.role === "external-hub" || node.role === "attached-device";
  return [
    ...path,
    node.title,
    node.subtitle,
    node.badge,
    roleLabel(node.role),
    port.protocol,
    port.speed,
    port.deviceId,
    port.display?.connectorTechnology,
    isDevice ? port.displayName : undefined,
    isDevice ? port.hardwareKind : undefined,
    isDevice ? port.manufacturer : undefined,
    isDevice ? port.display?.monitorName : undefined,
    isDevice ? port.usb?.productName : undefined,
    isDevice ? port.idResolution?.vendorName : undefined,
    isDevice ? port.idResolution?.deviceName : undefined,
    isDevice ? port.idResolution?.subsystemName : undefined,
    ...(node.specializedDevice?.searchTerms ?? []),
    ...(node.specializedDevice?.capabilityLabels ?? [])
  ].filter(Boolean).join(" ").toLocaleLowerCase();
}

function createsCycle(
  parent: PendingNode,
  child: PendingNode,
  connectors: ReadonlyMap<string, PendingNode>,
  devices: ReadonlyMap<string, PendingNode>
) {
  const seen = new Set([child.id]);
  let cursor: PendingNode | undefined = parent;
  while (cursor) {
    if (!seen.add(cursor.id)) {
      return true;
    }
    if (!cursor.parentId) {
      return false;
    }
    cursor = cursor.parentId.endsWith(":device")
      ? devices.get(portIdFromDeviceNodeId(cursor.parentId))
      : connectors.get(portIdFromConnectorNodeId(cursor.parentId));
  }
  return false;
}

function comparePendingNodes(left: PendingNode, right: PendingNode) {
  const leftPort = left.port.usb?.portNumber ?? Number.MAX_SAFE_INTEGER;
  const rightPort = right.port.usb?.portNumber ?? Number.MAX_SAFE_INTEGER;
  return leftPort - rightPort || left.title.localeCompare(right.title, undefined, { sensitivity: "base" });
}

function connectorNodeId(portId: string) {
  return `external:${portId}:connector`;
}

function deviceNodeId(portId: string) {
  return `external:${portId}:device`;
}

function portIdFromConnectorNodeId(nodeId: string) {
  return nodeId.slice("external:".length, -":connector".length);
}

function portIdFromDeviceNodeId(nodeId: string) {
  return nodeId.slice("external:".length, -":device".length);
}

function normalizeHubPath(value?: string | null) {
  let normalized = value?.trim()
    .replace(/^\\\\\.\\/, "\\\\?\\")
    .replace(/^\\\?\?\\/, "\\\\?\\");
  if (normalized && !normalized.startsWith("\\") && normalized.includes("#")) {
    normalized = `\\\\?\\${normalized}`;
  }
  return normalized?.toLocaleLowerCase() || undefined;
}

function externalInterfaceLabel(connectorKind: string) {
  switch (connectorKind.toLocaleLowerCase()) {
    case "usb-a": return "USB-A 接口";
    case "usb-c": return "USB-C 接口";
    case "thunderbolt": return "Thunderbolt 接口";
    case "hdmi": return "HDMI 接口";
    case "displayport": return "DisplayPort 接口";
    case "mini-displayport": return "Mini DisplayPort 接口";
    case "dvi": return "DVI 接口";
    case "vga": return "VGA 接口";
    case "rj45": return "RJ45 接口";
    case "audio": return "音频接口";
    case "power": return "电源接口";
    case "sd-card": return "SD 卡接口";
    default: return "USB 接口";
  }
}

function roleLabel(role: ExternalInterfaceNodeRole) {
  switch (role) {
    case "host-interface": return "本机接口";
    case "external-hub": return "USB Hub";
    case "downstream-interface": return "扩展接口";
    case "attached-device": return "连接设备";
  }
}

function resolveNonUsbConnectionState(port: DeviceTopologyPort): ExternalInterfaceConnectionState {
  if (port.display) {
    return port.display.active && port.display.targetAvailable ? "connected" : "disconnected";
  }
  if (port.network) {
    const state = port.network.connectionState.toLocaleLowerCase();
    if (state.includes("connected") || state === "up" || state.includes("已连接")) return "connected";
    if (state.includes("disconnected") || state === "down" || state.includes("未连接")) return "disconnected";
  }
  return port.speed.includes("未连接") ? "disconnected" : "unknown";
}

function resolveNonUsbStateLabel(port: DeviceTopologyPort) {
  const state = resolveNonUsbConnectionState(port);
  return state === "connected" ? "已连接" : state === "disconnected" ? "未连接" : "状态未知";
}

function meaningfulSpeed(value?: string | null) {
  const normalized = value?.trim();
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
