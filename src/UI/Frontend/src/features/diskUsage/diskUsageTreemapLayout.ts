import type { DiskUsageTreeFacts } from "./diskUsageTreeFacts.ts";
import type { DiskUsageLayout } from "./diskUsageLayoutTypes.ts";
import type { DiskUsageViewWindow } from "./diskUsageViewport.ts";

export const fullDiskUsageView: DiskUsageViewWindow = {
  pixelWidth: 1920, pixelHeight: 1080, scale: 1, minX: 0, minY: 0, maxX: 1, maxY: 1
};
export const maximumDiskUsageTiles = 400_000;
export const minimumTilePixelArea = 8;

/** Geometry and the pixel-area predicate share float32 intermediates with the original layout. */
export function diskUsageTilePixelArea(width: number, height: number, view: DiskUsageViewWindow): number {
  const f = Math.fround;
  return f(f(f(f(f(f(width) * f(view.pixelWidth)) * f(view.scale)) * f(height)) * f(view.pixelHeight)) * f(view.scale));
}

/** Iterative squarification and pruning. Runs only inside the page's Web Worker. */
export function layoutDiskUsageTree(
  tree: DiskUsageTreeFacts, rootNodeId = tree.rootNodeId, view = fullDiskUsageView
): DiskUsageLayout {
  view = {
    pixelWidth: Math.fround(view.pixelWidth), pixelHeight: Math.fround(view.pixelHeight),
    scale: Math.fround(view.scale), minX: Math.fround(view.minX), minY: Math.fround(view.minY),
    maxX: Math.fround(view.maxX), maxY: Math.fround(view.maxY)
  };
  const root = tree.indexByNodeId.get(rootNodeId);
  if (root === undefined) throw new Error("Disk tree node is unavailable");
  const capacity = Math.min(tree.subtreeNodeCounts[root], maximumDiskUsageTiles);
  const nodeIds = new Int32Array(capacity), parentIds = new Int32Array(capacity), depths = new Int32Array(capacity);
  const x = new Float32Array(capacity), y = new Float32Array(capacity);
  const width = new Float32Array(capacity), height = new Float32Array(capacity);
  const sizes = new Float64Array(capacity), fileCounts = new Float64Array(capacity);
  const directoryFlags = new Uint8Array(capacity), sourceIndices = new Uint32Array(capacity);
  const subtreeComplete = new Uint8Array(capacity);
  const names: string[] = [], indexByNodeId = new Map<number, number>();
  let count = 0, omittedCount = 0;
  function append(source: number, depth: number, left: number, top: number, w: number, h: number) {
    const index = count++;
    nodeIds[index] = tree.nodeIds[source]; parentIds[index] = tree.parentIds[source]; depths[index] = depth;
    x[index] = left; y[index] = top; width[index] = w; height[index] = h;
    sizes[index] = tree.sizes[source]; fileCounts[index] = tree.fileCounts[source];
    directoryFlags[index] = tree.directoryFlags[source]; sourceIndices[index] = source;
    subtreeComplete[index] = tree.subtreeComplete[source];
    names.push(tree.names[source]); indexByNodeId.set(nodeIds[index], index);
  }
  append(root, 0, 0, 0, 1, 1);
  for (let pending = 0; pending < count; pending++) {
    const source = sourceIndices[pending];
    if (directoryFlags[pending] !== 1) continue;
    const end = tree.childOffsets[source + 1];
    let child = tree.childOffsets[source], remaining = tree.childSizeTotals[source];
    let left = x[pending], top = y[pending], w = width[pending], h = height[pending];
    while (child < end && remaining > 0 && w > 0 && h > 0) {
      const alongWidth = w >= h, short = alongWidth ? h : w, long = alongWidth ? w : h;
      let rowEnd = child, sum = 0, largest = 0, best = Infinity;
      while (rowEnd < end) {
        const candidate = tree.sizes[tree.children[rowEnd]];
        const nextSum = sum + candidate;
        if (nextSum <= 0) { rowEnd++; continue; }
        const nextLargest = rowEnd === child ? candidate : largest;
        const thickness = nextSum / remaining * long;
        const maxExtent = nextLargest / nextSum * short;
        const minExtent = candidate / nextSum * short;
        const ratio = Math.max(thickness / maxExtent, maxExtent / thickness,
          minExtent > 0 ? thickness / minExtent : 0, minExtent / thickness);
        if (ratio > best) break;
        sum = nextSum; largest = nextLargest; best = ratio; rowEnd++;
      }
      if (rowEnd === child) break;
      const thickness = Math.fround(sum / remaining * long);
      let offset = alongWidth ? top : left;
      for (let at = child; at < rowEnd; at++) {
        const node = tree.children[at], extent = Math.fround(sum > 0 ? tree.sizes[node] / sum * short : 0);
        const tx = alongWidth ? left : offset, ty = alongWidth ? offset : top;
        const tw = alongWidth ? thickness : extent, th = alongWidth ? extent : thickness;
        if (Math.fround(tx + tw) < view.minX || tx > view.maxX || Math.fround(ty + th) < view.minY || ty > view.maxY
            || diskUsageTilePixelArea(tw, th, view) < minimumTilePixelArea
            || count >= capacity) omittedCount++;
        else append(node, depths[pending] + 1, tx, ty, tw, th);
        offset = Math.fround(offset + extent);
      }
      if (alongWidth) { left = Math.fround(left + thickness); w = Math.fround(w - thickness); }
      else { top = Math.fround(top + thickness); h = Math.fround(h - thickness); }
      remaining -= sum; child = rowEnd;
    }
  }
  const segments: string[] = [];
  for (let cursor = root; cursor !== 0; cursor = tree.indexByNodeId.get(tree.parentIds[cursor])!) segments.push(tree.names[cursor]);
  segments.reverse();
  const rootPath = segments.length === 0 ? tree.rootPath
    : tree.rootPath.replace(/\\?$/, "\\") + segments.join("\\");
  return {
    rootNodeId, rootPath, rootSizeBytes: tree.sizes[root], omittedCount, view,
    queryRootNodeId: tree.rootNodeId, nextCursorNodeId: tree.nextCursorNodeId,
    subtreeComplete: subtreeComplete.slice(0, count),
    nodeIds: nodeIds.slice(0, count), parentIds: parentIds.slice(0, count), depths: depths.slice(0, count),
    x: x.slice(0, count), y: y.slice(0, count), width: width.slice(0, count), height: height.slice(0, count),
    directoryFlags: directoryFlags.slice(0, count), sizes: sizes.slice(0, count), fileCounts: fileCounts.slice(0, count),
    names, indexByNodeId
  };
}

export function diskUsageLayoutTransfers(layout: DiskUsageLayout): ArrayBuffer[] {
  return [layout.nodeIds, layout.parentIds, layout.depths, layout.x, layout.y, layout.width,
    layout.height, layout.directoryFlags, layout.sizes, layout.fileCounts, layout.subtreeComplete].map((column) => column.buffer as ArrayBuffer);
}
