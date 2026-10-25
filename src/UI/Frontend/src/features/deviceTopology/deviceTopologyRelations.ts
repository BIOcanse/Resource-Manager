import type { DeviceTopologyPort } from "../../types";

// 端口之间的两种父子关系：
// - 设备关系：端口的 deviceId 指向 nativeParentDeviceId 和 upstreamDeviceId；
// - USB 集线器关系：端口的 usb.downstreamHubDevicePath（下游集线器）指向 usb.hubDevicePath（所在集线器）。
// 解码边界是这两张图无环的唯一检查者；树构建按无环输入直接使用。

export type DeviceTopologyRelationField =
  | "nativeParentDeviceId"
  | "upstreamDeviceId"
  | "usb.downstreamHubDevicePath";

export interface DeviceTopologyRelationCycle {
  /** 闭合环的那条关系所在的端口下标和字段。 */
  portIndex: number;
  field: DeviceTopologyRelationField;
}

interface RelationEdge {
  target: string;
  portIndex: number;
  field: DeviceTopologyRelationField;
}

export function normalizeDeviceId(value?: string | null) {
  return value?.trim().replaceAll("/", "\\").toLocaleUpperCase() ?? "";
}

export function normalizeHubPath(value?: string | null) {
  let normalized = value?.trim()
    .replace(/^\\\\\.\\/, "\\\\?\\")
    .replace(/^\\\?\?\\/, "\\\\?\\");
  if (normalized && !normalized.startsWith("\\") && normalized.includes("#")) {
    normalized = `\\\\?\\${normalized}`;
  }
  return normalized?.toLocaleLowerCase() || undefined;
}

/** 返回第一条闭合环（含自指）的关系；两张图都无环时返回 null。 */
export function findDeviceTopologyRelationCycle(
  ports: readonly DeviceTopologyPort[]
): DeviceTopologyRelationCycle | null {
  const deviceEdges = new Map<string, RelationEdge[]>();
  const hubEdges = new Map<string, RelationEdge[]>();
  ports.forEach((port, portIndex) => {
    const deviceId = normalizeDeviceId(port.deviceId);
    if (deviceId) {
      for (const field of ["nativeParentDeviceId", "upstreamDeviceId"] as const) {
        addEdge(deviceEdges, deviceId, normalizeDeviceId(port[field]), portIndex, field);
      }
    }
    const downstreamHub = normalizeHubPath(port.usb?.downstreamHubDevicePath);
    if (downstreamHub) {
      addEdge(hubEdges, downstreamHub, normalizeHubPath(port.usb?.hubDevicePath) ?? "", portIndex, "usb.downstreamHubDevicePath");
    }
  });
  const edge = findCycleEdge(deviceEdges) ?? findCycleEdge(hubEdges);
  return edge ? { portIndex: edge.portIndex, field: edge.field } : null;
}

function addEdge(
  edges: Map<string, RelationEdge[]>,
  from: string,
  target: string,
  portIndex: number,
  field: DeviceTopologyRelationField
) {
  if (!target) {
    return;
  }
  const list = edges.get(from) ?? [];
  list.push({ target, portIndex, field });
  edges.set(from, list);
}

// 显式栈深度优先：每个节点只入栈一次、每条边只看一次，步数以节点数加边数为界。
function findCycleEdge(edges: ReadonlyMap<string, readonly RelationEdge[]>): RelationEdge | null {
  const state = new Map<string, "open" | "closed">();
  for (const start of edges.keys()) {
    if (state.has(start)) {
      continue;
    }
    state.set(start, "open");
    const stack = [{ node: start, next: 0 }];
    while (stack.length > 0) {
      const frame = stack[stack.length - 1];
      const outgoing = edges.get(frame.node) ?? [];
      if (frame.next >= outgoing.length) {
        state.set(frame.node, "closed");
        stack.pop();
        continue;
      }
      const edge = outgoing[frame.next];
      frame.next += 1;
      const targetState = state.get(edge.target);
      if (targetState === "open") {
        return edge;
      }
      if (targetState === undefined) {
        state.set(edge.target, "open");
        stack.push({ node: edge.target, next: 0 });
      }
    }
  }
  return null;
}
