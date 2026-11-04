import { decodeDiskUsageTree } from "./diskUsageTreeFacts.ts";
import { diskUsageLayoutTransfers, layoutDiskUsageTree } from "./diskUsageTreemapLayout.ts";
import type { DiskUsageLayoutRequest, DiskUsageLayoutResponse } from "./diskUsageLayoutWorker.ts";
import type { DiskUsageTreeFacts } from "./diskUsageTreeFacts.ts";

let tree: DiskUsageTreeFacts | null = null;
const scope = globalThis as unknown as {
  onmessage: (event: MessageEvent<DiskUsageLayoutRequest>) => void;
  postMessage: (message: DiskUsageLayoutResponse, transfer?: Transferable[]) => void;
};
scope.onmessage = ({ data }) => {
  try {
    if (data.buffer) tree = decodeDiskUsageTree(data.buffer);
    if (tree && data.rootNodeId !== undefined && !tree.indexByNodeId.has(data.rootNodeId)) {
      scope.postMessage({ id: data.id, queryNodeId: data.rootNodeId });
      return;
    }
    const layout = tree ? layoutDiskUsageTree(tree, data.rootNodeId, data.view) : null;
    scope.postMessage({ id: data.id, layout }, layout ? diskUsageLayoutTransfers(layout) : []);
  } catch (cause) {
    scope.postMessage({ id: data.id, error: cause instanceof Error ? cause.message : String(cause) });
  }
};
