import type { DeviceTopologyPort } from "../../types";

// Ports form two parent-child graphs.
// Device edges use deviceId, nativeParentDeviceId, and upstreamDeviceId.
// USB hub edges connect downstreamHubDevicePath to hubDevicePath.
// The decoder alone validates acyclicity before tree construction.

export type DeviceTopologyRelationField =
  | "nativeParentDeviceId"
  | "upstreamDeviceId"
  | "usb.downstreamHubDevicePath";

export interface DeviceTopologyRelationCycle {
  /** Port index and field of the edge that closes a cycle. */
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

/** Return the first cycle-closing edge, including self-links, or null if both graphs are acyclic. */
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

// Iterative depth-first traversal visits each node and edge once.
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
