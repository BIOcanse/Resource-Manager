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
  while (cursor && !included.has(cursor.id)) {
    included.add(cursor.id);
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
    if (included.has(child.id)) {
      continue;
    }
    included.add(child.id);
    stack.push(...(childrenByParent.get(child.id) ?? []));
  }
}

/**
 * 先序遍历构建中的树：父节点先于子节点访问，visit 返回传给子节点的上下文；
 * compareChildren 给出时子节点按它的顺序访问。树由构建方保证无环（见 deviceTopologyRelations.ts），
 * nodeLimit 是遍历的硬上限，超过即说明输入违反了这一保证。
 */
export function visitPendingTree<TNode extends { children: readonly TNode[] }, TContext>(
  roots: readonly TNode[],
  rootContext: TContext,
  nodeLimit: number,
  visit: (node: TNode, context: TContext) => TContext,
  compareChildren?: (left: TNode, right: TNode) => number
) {
  const stack = roots.map((node) => ({ node, context: rootContext })).reverse();
  let visited = 0;
  while (stack.length > 0) {
    const { node, context } = stack.pop()!;
    visited += 1;
    if (visited > nodeLimit) {
      throw new Error(`Device topology tree exceeds ${nodeLimit} nodes; its port relations are not a tree.`);
    }
    const childContext = visit(node, context);
    const children = compareChildren ? [...node.children].sort(compareChildren) : node.children;
    for (let index = children.length - 1; index >= 0; index -= 1) {
      stack.push({ node: children[index], context: childContext });
    }
  }
}
