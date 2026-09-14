import type { DeviceTopologyPort } from "../types";
import { resolveSpecializedDevice } from "./adapters/adapterRegistry.ts";
import {
  buildExternalInterfaceTree,
  type ExternalInterfaceTreeSummary
} from "./externalInterfaceTree.ts";
import {
  filterDeviceTopologyTree,
  type DeviceTopologyTreeNode
} from "./deviceTopologyTree.ts";

export type InternalInterfaceNodeRole =
  | "internal-interface"
  | "internal-controller"
  | "internal-device"
  | "internal-function";

export interface InternalInterfaceTreeNode extends DeviceTopologyTreeNode<InternalInterfaceNodeRole> {}

export interface InternalInterfaceTreeSummary {
  nodes: InternalInterfaceTreeNode[];
  interfaceCount: number;
  controllerCount: number;
  attachedDeviceCount: number;
}

interface PendingNode extends Omit<InternalInterfaceTreeNode, "depth" | "path" | "searchText"> {
  children: PendingNode[];
}

interface InterfaceDescriptor {
  key: string;
  title: string;
  connectorKind: string;
  protocol: string;
}

const controllerServices = new Set([
  "stornvme",
  "storahci",
  "ucmucsiaacpidevice",
  "ucmucsiacpiclient",
  "usb4devicerouter",
  "usb4hostrouter",
  "usbxhci",
  "usbhub3"
]);

const virtualServices = new Set(["vwifimp", "vhdmp", "mskssrv"]);

export function buildInternalInterfaceTree(
  ports: readonly DeviceTopologyPort[],
  externalTree: ExternalInterfaceTreeSummary = buildExternalInterfaceTree(ports)
): InternalInterfaceTreeSummary {
  const externalPortIds = new Set(externalTree.nodes.map((node) => node.port.id));
  const externalDeviceIds = new Set(
    externalTree.nodes
      .filter((node) => node.role === "external-hub" || node.role === "attached-device")
      .map((node) => normalizeDeviceId(node.port.deviceId))
      .filter(Boolean)
  );
  const allByDeviceId = indexPortsByDeviceId(ports);
  const candidates = ports
    .filter((port) => !externalPortIds.has(port.id))
    .filter((port) => !isDescendantOfExternalDevice(port, externalDeviceIds, allByDeviceId))
    .filter(isInternalHardwareCandidate)
    .sort(comparePorts);
  const candidateByDeviceId = indexPortsByDeviceId(candidates);
  const nodesByPortId = new Map<string, PendingNode>();

  for (const port of candidates) {
    nodesByPortId.set(port.id, createDeviceNode(port));
  }

  const parentPortIdByPortId = new Map<string, string>();
  for (const port of candidates) {
    const parent = resolveVisibleParent(port, candidateByDeviceId);
    if (parent && parent.id !== port.id) {
      parentPortIdByPortId.set(port.id, parent.id);
    }
  }

  const orphanedNodes: PendingNode[] = [];
  for (const port of candidates) {
    const node = nodesByPortId.get(port.id)!;
    const parentPortId = parentPortIdByPortId.get(port.id);
    const parent = parentPortId ? nodesByPortId.get(parentPortId) : undefined;
    if (!parent || createsParentCycle(port.id, parentPortIdByPortId)) {
      orphanedNodes.push(node);
      continue;
    }

    node.parentId = parent.id;
    parent.children.push(node);
  }

  const interfaceGroups = new Map<string, { descriptor: InterfaceDescriptor; children: PendingNode[] }>();
  for (const node of orphanedNodes) {
    const descriptor = describeInternalInterface(node.port);
    const group = interfaceGroups.get(descriptor.key) ?? { descriptor, children: [] };
    group.children.push(node);
    interfaceGroups.set(descriptor.key, group);
  }

  const titleCounts = countBy([...interfaceGroups.values()].map((group) => group.descriptor.title));
  const titleOrdinals = new Map<string, number>();
  const roots = [...interfaceGroups.values()]
    .sort((left, right) => compareInterfaceDescriptors(left.descriptor, right.descriptor))
    .map((group) => {
      const ordinal = (titleOrdinals.get(group.descriptor.title) ?? 0) + 1;
      titleOrdinals.set(group.descriptor.title, ordinal);
      const title = (titleCounts.get(group.descriptor.title) ?? 0) > 1
        ? `${group.descriptor.title} ${ordinal}`
        : group.descriptor.title;
      const root = createInterfaceNode(group.descriptor, group.children[0].port, title, group.children);
      for (const child of group.children) {
        child.parentId = root.id;
      }
      return root;
    });

  for (const root of roots) {
    applySpecializedDevicePresentation(root);
  }

  const flattened: InternalInterfaceTreeNode[] = [];
  const visited = new Set<string>();
  for (const root of roots) {
    appendNode(root, 0, [], flattened, visited);
  }

  return {
    nodes: flattened,
    interfaceCount: flattened.filter((node) => node.role === "internal-interface").length,
    controllerCount: flattened.filter((node) => node.role === "internal-controller").length,
    attachedDeviceCount: flattened.filter((node) => node.role !== "internal-interface").length
  };
}

export function filterInternalInterfaceTree(
  nodes: readonly InternalInterfaceTreeNode[],
  query: string
): InternalInterfaceTreeNode[] {
  return filterDeviceTopologyTree(nodes, query);
}

export function internalInterfaceRoleLabel(role: InternalInterfaceNodeRole) {
  switch (role) {
    case "internal-interface":
      return "内部接口";
    case "internal-controller":
      return "控制器";
    case "internal-function":
      return "设备功能";
    default:
      return "连接设备";
  }
}

function createDeviceNode(port: DeviceTopologyPort): PendingNode {
  const role = resolveDeviceRole(port);
  return {
    id: deviceNodeId(port.id),
    port,
    role,
    title: port.displayName,
    subtitle: joinSummary(port.hardwareKind, meaningfulSpeed(port.speed)),
    badge: internalInterfaceRoleLabel(role),
    connectorKind: inferDeviceConnectorKind(port),
    connectionState: "connected",
    children: []
  };
}

function createInterfaceNode(
  descriptor: InterfaceDescriptor,
  representativePort: DeviceTopologyPort,
  title: string,
  children: PendingNode[]
): PendingNode {
  return {
    id: interfaceNodeId(descriptor.key),
    port: representativePort,
    role: "internal-interface",
    title,
    subtitle: `${children.length} 个直属设备 · ${descriptor.protocol}`,
    badge: "内部接口",
    connectorKind: descriptor.connectorKind,
    connectionState: "connected",
    children
  };
}

function applySpecializedDevicePresentation(node: PendingNode, parentTitle?: string) {
  if (node.role !== "internal-interface") {
    const model = resolveSpecializedDevice({
      scope: "internal",
      port: node.port,
      fallbackTitle: node.title,
      parentTitle,
      downstreamInterfaceCount: node.children.length,
      connectedDownstreamInterfaceCount: node.children.length
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

function appendNode(
  node: PendingNode,
  depth: number,
  parentPath: readonly string[],
  output: InternalInterfaceTreeNode[],
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
  return [
    ...path,
    node.title,
    node.subtitle,
    node.badge,
    internalInterfaceRoleLabel(node.role),
    port.displayName,
    port.hardwareKind,
    port.protocol,
    port.speed,
    port.busKind,
    port.deviceId,
    port.pnpClass,
    port.service,
    port.manufacturer,
    port.display?.monitorName,
    port.network?.interfaceName,
    port.storage?.model,
    port.idResolution?.vendorName,
    port.idResolution?.deviceName,
    port.idResolution?.subsystemName,
    ...(node.specializedDevice?.searchTerms ?? []),
    ...(node.specializedDevice?.capabilityLabels ?? [])
  ].filter(Boolean).join(" ").toLocaleLowerCase();
}

function resolveVisibleParent(
  port: DeviceTopologyPort,
  candidatesByDeviceId: ReadonlyMap<string, DeviceTopologyPort>
) {
  for (const value of [port.nativeParentDeviceId, port.upstreamDeviceId]) {
    const parent = candidatesByDeviceId.get(normalizeDeviceId(value));
    if (parent) {
      return parent;
    }
  }
  return undefined;
}

function isDescendantOfExternalDevice(
  port: DeviceTopologyPort,
  externalDeviceIds: ReadonlySet<string>,
  allByDeviceId: ReadonlyMap<string, DeviceTopologyPort>
) {
  const pending = [port.nativeParentDeviceId, port.upstreamDeviceId]
    .map(normalizeDeviceId)
    .filter(Boolean);
  const visited = new Set<string>();
  while (pending.length > 0 && visited.size < 32) {
    const current = pending.shift()!;
    if (!visited.add(current)) {
      continue;
    }
    if (externalDeviceIds.has(current)) {
      return true;
    }
    const parent = allByDeviceId.get(current);
    if (!parent) {
      continue;
    }
    pending.push(
      ...[parent.nativeParentDeviceId, parent.upstreamDeviceId]
        .map(normalizeDeviceId)
        .filter(Boolean)
    );
  }
  return false;
}

function isInternalHardwareCandidate(port: DeviceTopologyPort) {
  if (port.display?.internal === true) {
    return true;
  }
  if (port.display?.internal === false) {
    return false;
  }
  if (port.network?.hardwareInterface === false) {
    return false;
  }

  const service = port.service?.trim().toLocaleLowerCase();
  if (service && virtualServices.has(service)) {
    return false;
  }

  const deviceId = normalizeDeviceId(port.deviceId);
  if (!deviceId
    || /^(SW|ROOT|HTREE|STORAGE\\VOLUME|\{)/.test(deviceId)
    || /WI-FI DIRECT VIRTUAL|VHD|STREAMING PROXY/i.test(port.displayName)) {
    return false;
  }

  return /^(PCI|USB|HDAUDIO|ACPI|BTH|BTHENUM|BTHLE|BTHLEDEVICE|BTHHFENUM|HID|SCSI)\\/.test(deviceId)
    || port.storage != null
    || port.camera != null
    || port.hid != null
    || port.advancedInterconnect != null;
}

function resolveDeviceRole(port: DeviceTopologyPort): Exclude<InternalInterfaceNodeRole, "internal-interface"> {
  if (isControllerPort(port)) {
    return "internal-controller";
  }
  if (/(&MI_\d+|&COL\d+|BTHLEDEVICE\\|BTHHFENUM\\)/i.test(port.deviceId)
    || port.pnpClass?.toLocaleLowerCase() === "ports") {
    return "internal-function";
  }
  return "internal-device";
}

function isControllerPort(port: DeviceTopologyPort) {
  if (port.advancedInterconnect || port.usb?.deviceIsHub === true) {
    return true;
  }
  const service = port.service?.trim().toLocaleLowerCase();
  if (service && controllerServices.has(service)) {
    return true;
  }
  if (["scsiadapter", "ucm"].includes(port.pnpClass?.trim().toLocaleLowerCase() ?? "")) {
    return true;
  }
  return /controller|host router|root hub|控制器|连接器管理器|根集线器/i.test(`${port.displayName} ${port.hardwareKind}`);
}

function describeInternalInterface(port: DeviceTopologyPort): InterfaceDescriptor {
  const parentIdentity = normalizeDeviceId(port.nativeParentDeviceId)
    || normalizeDeviceId(port.upstreamDeviceId)
    || normalizeDeviceId(port.deviceId)
    || port.id;
  if (port.display?.internal === true) {
    return {
      key: `display:${port.id}`,
      title: internalDisplayInterfaceTitle(port),
      connectorKind: "internal-display",
      protocol: port.display.connectorTechnology || "内置显示"
    };
  }
  if (/^HDAUDIO\\/.test(normalizeDeviceId(port.deviceId))) {
    return { key: `hda:${parentIdentity}`, title: "HD Audio 内部接口", connectorKind: "audio", protocol: "HD Audio" };
  }
  if (port.busKind.toLocaleLowerCase() === "bluetooth" || /^BTH/.test(normalizeDeviceId(port.deviceId))) {
    return { key: `bluetooth:${parentIdentity}`, title: "Bluetooth 内部接口", connectorKind: "bluetooth", protocol: "Bluetooth" };
  }
  if (/^PCI\\/.test(normalizeDeviceId(port.deviceId)) || /^PCI\\/.test(parentIdentity)) {
    return { key: `pcie:${parentIdentity}`, title: "PCIe 内部接口", connectorKind: "pcie", protocol: "PCI Express" };
  }
  if (port.busKind.toLocaleLowerCase() === "usb" || /^USB\\/.test(normalizeDeviceId(port.deviceId))) {
    return { key: `usb:${parentIdentity}`, title: "内部 USB 接口", connectorKind: "usb-internal", protocol: "USB" };
  }
  if (/^ACPI\\/.test(normalizeDeviceId(port.deviceId)) || /^ACPI\\/.test(parentIdentity)) {
    return { key: `acpi:${parentIdentity}`, title: "ACPI 内部接口", connectorKind: "acpi", protocol: "ACPI" };
  }
  if (port.busKind.toLocaleLowerCase() === "storage") {
    return { key: `storage:${parentIdentity}`, title: "存储内部接口", connectorKind: "storage", protocol: port.storage?.busType ?? port.protocol };
  }
  return { key: `system:${parentIdentity}`, title: "系统内部接口", connectorKind: "internal", protocol: port.protocol };
}

function internalDisplayInterfaceTitle(port: DeviceTopologyPort) {
  const technology = port.display?.connectorTechnology?.trim().toLocaleLowerCase() ?? "";
  return technology.includes("displayport") || technology.includes("edp")
    ? "eDP 内部接口"
    : "内置显示接口";
}

function inferDeviceConnectorKind(port: DeviceTopologyPort) {
  if (port.display?.internal) return "monitor";
  if (port.storage) return "hard-drive";
  if (port.camera) return "camera";
  if (port.hid?.hidType.includes("键盘")) return "keyboard";
  if (port.hid?.hidType.includes("鼠标")) return "mouse";
  if (port.busKind === "display") return "pcie";
  if (port.busKind === "network") return "network-adapter";
  if (port.busKind === "bluetooth") return "bluetooth";
  if (port.busKind === "audio") return "audio";
  if (isControllerPort(port)) return "controller";
  return port.connectorKind || "device";
}

function createsParentCycle(portId: string, parents: ReadonlyMap<string, string>) {
  const visited = new Set([portId]);
  let cursor = parents.get(portId);
  while (cursor) {
    if (!visited.add(cursor)) {
      return true;
    }
    cursor = parents.get(cursor);
  }
  return false;
}

function indexPortsByDeviceId(ports: readonly DeviceTopologyPort[]) {
  const result = new Map<string, DeviceTopologyPort>();
  for (const port of ports) {
    const key = normalizeDeviceId(port.deviceId);
    if (key && !result.has(key)) {
      result.set(key, port);
    }
  }
  return result;
}

function countBy(values: readonly string[]) {
  const result = new Map<string, number>();
  for (const value of values) {
    result.set(value, (result.get(value) ?? 0) + 1);
  }
  return result;
}

function compareInterfaceDescriptors(left: InterfaceDescriptor, right: InterfaceDescriptor) {
  return left.title.localeCompare(right.title, undefined, { sensitivity: "base" })
    || left.key.localeCompare(right.key);
}

function comparePendingNodes(left: PendingNode, right: PendingNode) {
  return roleRank(left.role) - roleRank(right.role)
    || left.title.localeCompare(right.title, undefined, { sensitivity: "base" })
    || left.id.localeCompare(right.id);
}

function comparePorts(left: DeviceTopologyPort, right: DeviceTopologyPort) {
  return left.displayName.localeCompare(right.displayName, undefined, { sensitivity: "base" })
    || left.id.localeCompare(right.id);
}

function roleRank(role: InternalInterfaceNodeRole) {
  switch (role) {
    case "internal-controller": return 0;
    case "internal-device": return 1;
    case "internal-function": return 2;
    default: return 3;
  }
}

function interfaceNodeId(key: string) {
  return `internal:interface:${stableHash(key)}`;
}

function deviceNodeId(portId: string) {
  return `internal:${portId}:device`;
}

function stableHash(value: string) {
  let hash = 2166136261;
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0).toString(16).padStart(8, "0");
}

function normalizeDeviceId(value?: string | null) {
  return value?.trim().replaceAll("/", "\\").toLocaleUpperCase() ?? "";
}

function meaningfulSpeed(value?: string | null) {
  const normalized = value?.trim();
  return !normalized || normalized === "不适用" || normalized === "未知" || normalized === "未连接"
    ? undefined
    : normalized;
}

function joinSummary(...values: Array<string | null | undefined>) {
  return values.map((value) => value?.trim()).filter(Boolean).join(" · ");
}
