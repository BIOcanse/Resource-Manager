import { portDisplayName, portHardwareKind, transportLabel } from "./deviceVocabulary.ts";
import type { DeviceTopologyPort } from "../../types";
import { resolveSpecializedDevice } from "./adapters/adapterRegistry.ts";
import {
  buildExternalInterfaceTree,
  type ExternalInterfaceTreeSummary
} from "./externalInterfaceTree.ts";
import {
  filterDeviceTopologyTree,
  visitPendingTree,
  type DeviceTopologyTreeNode
} from "./deviceTopologyTree.ts";
import { normalizeDeviceId } from "./deviceTopologyRelations.ts";
import { uiText } from "../../text.ts";

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

  // 解码边界已保证设备关系无环，可见父设备直接挂接。
  const orphanedNodes: PendingNode[] = [];
  for (const port of candidates) {
    const node = nodesByPortId.get(port.id)!;
    const parentPort = resolveVisibleParent(port, candidateByDeviceId);
    const parent = parentPort ? nodesByPortId.get(parentPort.id) : undefined;
    if (!parent) {
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

  // 每个端口最多产生一个设备节点和一个接口节点。
  const nodeLimit = 2 * ports.length;
  visitPendingTree<PendingNode, string | undefined>(roots, undefined, nodeLimit, (node, parentTitle) => {
    applySpecializedDevicePresentation(node, parentTitle);
    return node.title;
  });

  const flattened: InternalInterfaceTreeNode[] = [];
  visitPendingTree<PendingNode, readonly string[]>(roots, [], nodeLimit, (node, parentPath) => {
    const path = [...parentPath, node.title];
    flattened.push(createTreeNode(node, parentPath.length, path));
    return path;
  }, comparePendingNodes);

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
      return uiText.deviceTree.internalInterface;
    case "internal-controller":
      return uiText.deviceTree.controller;
    case "internal-function":
      return uiText.deviceTree.deviceFunction;
    default:
      return uiText.deviceTree.attachedDevice;
  }
}

function createDeviceNode(port: DeviceTopologyPort): PendingNode {
  const role = resolveDeviceRole(port);
  return {
    id: deviceNodeId(port.id),
    port,
    role,
    title: portDisplayName(port),
    subtitle: joinSummary(portHardwareKind(port), meaningfulSpeed(port.speed)),
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
    subtitle: uiText.deviceTree.directDeviceSummary(children.length, descriptor.protocol),
    badge: uiText.deviceTree.internalInterface,
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
}

function createTreeNode(node: PendingNode, depth: number, path: string[]): InternalInterfaceTreeNode {
  return {
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
  };
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
  // 每个设备 ID 只展开一次，步数以不同设备 ID 的数量为界。
  const visited = new Set<string>();
  while (pending.length > 0) {
    const current = pending.shift()!;
    if (visited.has(current)) {
      continue;
    }
    visited.add(current);
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

  // The backend can identify a transport for an opaque device ID.
  if (port.transport.code !== null) return true;

  const service = port.service?.trim().toLocaleLowerCase();
  if (service && virtualServices.has(service)) {
    return false;
  }

  const deviceId = normalizeDeviceId(port.deviceId);
  if (!deviceId
    || /^(SW|ROOT|HTREE|STORAGE\\VOLUME|\{)/.test(deviceId)
    || /WI-FI DIRECT VIRTUAL|VHD|STREAMING PROXY/i.test(port.displayName ?? "")) {
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
  return ["scsiadapter", "ucm"].includes(port.pnpClass?.trim().toLocaleLowerCase() ?? "");
}

function describeInternalInterface(port: DeviceTopologyPort): InterfaceDescriptor {
  const parentIdentity = normalizeDeviceId(port.nativeParentDeviceId)
    || normalizeDeviceId(port.upstreamDeviceId)
    || normalizeDeviceId(port.deviceId)
    || port.id;
  const code = port.transport.code;
  const protocol = transportLabel(port.transport);
  if (port.display?.internal === true) {
    return {
      key: `display:${port.id}`,
      title: internalDisplayInterfaceTitle(port),
      connectorKind: "internal-display",
      protocol
    };
  }
  const key = `${code ?? `unknown:${port.transport.reason}`}:${parentIdentity}`;
  switch (code) {
    case "hd_audio": return { key, title: uiText.deviceTree.hdAudioInterface, connectorKind: "audio", protocol };
    case "bluetooth": return { key, title: uiText.deviceTree.bluetoothInterface, connectorKind: "bluetooth", protocol };
    case "pci": return { key, title: uiText.deviceTree.pciInterface, connectorKind: "internal", protocol };
    case "usb": return { key, title: uiText.deviceTree.internalUsbInterface, connectorKind: "usb-internal", protocol };
    case "acpi": return { key, title: uiText.deviceTree.acpiInterface, connectorKind: "acpi", protocol };
    default: return { key, title: protocol, connectorKind: "internal", protocol };
  }
}

function internalDisplayInterfaceTitle(port: DeviceTopologyPort) {
  const technology = (port.display?.outputTechnology ?? "").trim().toLocaleLowerCase();
  return technology.includes("displayport") || technology.includes("edp")
    ? uiText.deviceTree.embeddedDisplayPortInterface
    : uiText.deviceTree.internalDisplayInterface;
}

function inferDeviceConnectorKind(port: DeviceTopologyPort) {
  if (port.display?.internal) return "monitor";
  if (port.storage) return "hard-drive";
  if (port.camera) return "camera";
  if (port.hid?.hidType === "keyboard") return "keyboard";
  if (port.hid?.hidType === "mouse") return "mouse";
  if (port.pnpClass?.toLocaleLowerCase() === "display") return "graphics-card";
  if (port.network != null) return "network-adapter";
  if (port.transport.code === "bluetooth") return "bluetooth";
  if (port.transport.code === "hd_audio") return "audio";
  if (isControllerPort(port)) return "controller";
  return port.connectorKind || "device";
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
  return portDisplayName(left).localeCompare(portDisplayName(right), undefined, { sensitivity: "base" })
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

function meaningfulSpeed(value?: string | null) {
  const normalized = value?.trim();
  return !normalized || normalized === "不适用" || normalized === "未知" || normalized === "未连接"
    ? undefined
    : normalized;
}

function joinSummary(...values: Array<string | null | undefined>) {
  return values.map((value) => value?.trim()).filter(Boolean).join(" · ");
}
