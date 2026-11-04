import type { DiskUsageLayout } from "./diskUsageLayoutTypes.ts";

/** Resolve tile facts from layout data without per-hover backend requests. */
export interface DiskUsageTileFacts {
  nodeId: number;
  name: string;
  fullPath: string;
  isDirectory: boolean;
  sizeBytes: number;
  fileCount: number;
  subtreeComplete: boolean;
}

/** Bound parent traversal to prevent malformed cycles from looping. */
const maximumPathDepth = 512;

export function tileFactsOf(
  layout: DiskUsageLayout,
  nodeId: number
): DiskUsageTileFacts | undefined {
  const index = layout.indexByNodeId.get(nodeId);
  if (index === undefined) {
    return undefined;
  }
  return {
    nodeId,
    name: layout.names[index],
    fullPath: pathOf(layout, index),
    isDirectory: layout.directoryFlags[index] === 1,
    sizeBytes: layout.sizes[index],
    fileCount: layout.fileCounts[index],
    subtreeComplete: layout.subtreeComplete[index] === 1
  };
}

/** Build paths from parent links, including rootPath exactly once. */
export function pathOf(layout: DiskUsageLayout, index: number): string {
  if (layout.depths[index] === 0) {
    return layout.rootPath;
  }

  const segments: string[] = [];
  let cursor = index;
  let guard = 0;
  while (cursor !== undefined && guard++ < maximumPathDepth) {
    if (layout.depths[cursor] === 0) {
      break;
    }
    segments.push(layout.names[cursor]);
    const parent = layout.indexByNodeId.get(layout.parentIds[cursor]);
    if (parent === undefined || parent === cursor) {
      break;
    }
    cursor = parent;
  }

  segments.reverse();
  // Volume roots include a trailing separator; subtree roots may not.
  const prefix = layout.rootPath.endsWith("\\")
    ? layout.rootPath
    : `${layout.rootPath}\\`;
  return prefix + segments.join("\\");
}
