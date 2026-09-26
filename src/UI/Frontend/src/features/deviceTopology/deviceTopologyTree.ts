import type { SpecializedDeviceModel } from "./adapters/adapterRegistry.ts";
import type { DeviceTopologyPort } from "../../types";

export type DeviceTopologyTreeConnectionState = "connected" | "disconnected" | "unknown";

export interface DeviceTopologyTreeNode<TRole extends string = string> {
  id: string;
  port: DeviceTopologyPort;
  role: TRole;
  parentId?: string;
  depth: number;
  title: string;
  subtitle: string;
  badge: string;
  connectorKind: string;
  iconKind?: string;
  connectionState: DeviceTopologyTreeConnectionState;
  path: string[];
  searchText: string;
  specializedDevice?: SpecializedDeviceModel;
}

export function filterDeviceTopologyTree<TNode extends DeviceTopologyTreeNode>(
  nodes: readonly TNode[],
  query: string
): TNode[] {
  const normalizedQuery = query.trim().toLocaleLowerCase();
  if (!normalizedQuery) {
    return [...nodes];
  }

  const byId = new Map(nodes.map((node) => [node.id, node]));
  const childrenByParent = new Map<string, TNode[]>();
  for (const node of nodes) {
    if (!node.parentId) {
      continue;
    }
    const children = childrenByParent.get(node.parentId) ?? [];
    children.push(node);
    childrenByParent.set(node.parentId, children);
  }

  const included = new Set<string>();
  for (const node of nodes) {
    if (!node.searchText.includes(normalizedQuery)) {
      continue;
    }

    includeAncestors(node, byId, included);
    includeDescendants(node.id, childrenByParent, included);
  }

  return nodes.filter((node) => included.has(node.id));
}

function includeAncestors<TNode extends DeviceTopologyTreeNode>(
  node: TNode,
  byId: ReadonlyMap<string, TNode>,
  included: Set<string>
) {
  let cursor: TNode | undefined = node;
  while (cursor && included.add(cursor.id)) {
    cursor = cursor.parentId ? byId.get(cursor.parentId) : undefined;
  }
}

function includeDescendants<TNode extends DeviceTopologyTreeNode>(
  nodeId: string,
  childrenByParent: ReadonlyMap<string, readonly TNode[]>,
  included: Set<string>
) {
  const stack = [...(childrenByParent.get(nodeId) ?? [])];
  while (stack.length > 0) {
    const child = stack.pop()!;
    if (!included.add(child.id)) {
      continue;
    }
    stack.push(...(childrenByParent.get(child.id) ?? []));
  }
}
