import type { DiskUsageLayout } from "./diskUsageLayoutTypes.ts";
import type { DiskUsageViewWindow } from "./diskUsageViewport.ts";

export interface DiskUsageLayoutRequest {
  id: number;
  buffer?: ArrayBuffer;
  rootNodeId?: number;
  view?: DiskUsageViewWindow;
}
export type DiskUsageLayoutResult = DiskUsageLayout | null | { queryNodeId: number };
export type DiskUsageLayoutResponse = { id: number; layout: DiskUsageLayout | null } | { id: number; queryNodeId: number } | { id: number; error: string };
interface PendingLayout {
  request: DiskUsageLayoutRequest;
  resolve: (layout: DiskUsageLayoutResult) => void;
  reject: (cause: Error) => void;
}

/** One in-flight layout and one latest requested view. The page owns and disposes the worker. */
export class DiskUsageLayoutWorker {
  private worker: Worker | null = null;
  private active: PendingLayout | null = null;
  private queued: PendingLayout | null = null;
  private sequence = 0;

  replaceTree(buffer: ArrayBuffer, rootNodeId?: number): Promise<DiskUsageLayout | null> {
    this.dispose();
    const worker = new Worker(new URL("./diskUsageTreemap.worker.ts", import.meta.url), { type: "module" });
    this.worker = worker;
    worker.onmessage = ({ data }: MessageEvent<DiskUsageLayoutResponse>) => {
      const active = this.active;
      if (!active || active.request.id !== data.id) return;
      this.active = null;
      if ("error" in data) active.reject(new Error(data.error));
      else active.resolve("queryNodeId" in data ? { queryNodeId: data.queryNodeId } : data.layout);
      const queued = this.queued;
      this.queued = null;
      if (queued) this.send(queued);
    };
    worker.onerror = (event) => {
      const error = new Error(event.message || "Disk layout worker failed");
      this.active?.reject(error); this.queued?.reject(error);
      this.active = this.queued = null;
      worker.terminate(); this.worker = null;
    };
    return this.request({ id: ++this.sequence, buffer, rootNodeId }).then(result => {
      if (result && "queryNodeId" in result) throw new Error("The queried disk node is missing from its fact page");
      return result;
    });
  }

  layout(rootNodeId: number, view?: DiskUsageViewWindow): Promise<DiskUsageLayoutResult> {
    return this.request({ id: ++this.sequence, rootNodeId, view });
  }

  dispose() {
    this.worker?.terminate(); this.worker = null;
    const error = new DOMException("Disk layout superseded or disposed", "AbortError");
    this.active?.reject(error); this.queued?.reject(error);
    this.active = this.queued = null;
  }

  private request(request: DiskUsageLayoutRequest): Promise<DiskUsageLayoutResult> {
    if (!this.worker) return Promise.reject(new Error("Disk layout worker is unavailable"));
    return new Promise((resolve, reject) => {
      const pending = { request, resolve, reject };
      if (this.active) {
        this.queued?.reject(new DOMException("Disk layout superseded", "AbortError"));
        this.queued = pending;
      } else this.send(pending);
    });
  }

  private send(pending: PendingLayout) {
    this.active = pending;
    this.worker!.postMessage(pending.request, pending.request.buffer ? [pending.request.buffer] : []);
  }
}
