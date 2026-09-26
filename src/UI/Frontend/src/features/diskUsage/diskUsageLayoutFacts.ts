import type { DiskUsageLayout } from "./diskUsageLayoutTypes.ts";

/**
 * 从布局里直接取一个方格的事实。
 *
 * 布局已经带着名字、大小、是不是目录和父子关系，所以悬停提示要显示的东西
 * 这里全都有，不用再为每个方格去问一次后端 —— 指针扫过一片小方格时，
 * 那会变成几百次请求，而且每次回来都要重画一遍整张图。
 */
export interface DiskUsageTileFacts {
  nodeId: number;
  name: string;
  fullPath: string;
  isDirectory: boolean;
  sizeBytes: number;
  fileCount: number;
}

/** 路径最多往上走这么多层。防的是父子引用成环时转不出来。 */
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
    fileCount: layout.fileCounts[index]
  };
}

/**
 * 顺着父引用往上拼出完整路径。
 *
 * 根方格（深度 0）代表的就是 rootPath 本身，所以它不贡献名字段，
 * 只作为前缀出现一次。
 */
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
  // rootPath 形如 "C:\"，已经自带分隔符；子树的根则不一定。
  const prefix = layout.rootPath.endsWith("\\")
    ? layout.rootPath
    : `${layout.rootPath}\\`;
  return prefix + segments.join("\\");
}
