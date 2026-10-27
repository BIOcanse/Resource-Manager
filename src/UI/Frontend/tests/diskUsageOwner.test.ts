import assert from "node:assert/strict";
import { createDiskUsageOwner } from "../src/features/diskUsage/diskUsageOwner.ts";
import { BackendSessionOwner } from "../src/frontendRuntime/session/BackendSessionOwner.ts";
import { SourceRegistry } from "../src/frontendRuntime/source/SourceRegistry.ts";
import type { RequestClient } from "../src/frontendRuntime/request/RequestClient.ts";
import type { OperationInvalidation, OperationRegistry, OperationRegistrySnapshot } from "../src/frontendRuntime/operations/OperationRegistry.ts";
import { decodeDiskUsageTree } from "../src/features/diskUsage/diskUsageTreeFacts.ts";
import { layoutDiskUsageTree } from "../src/features/diskUsage/diskUsageTreemapLayout.ts";
import type { DiskUsageViewWindow } from "../src/features/diskUsage/diskUsageViewport.ts";
import { diskTreeBuffer } from "./diskUsageTreeFixture.ts";

const session = new BackendSessionOwner({ transport: null, allowStandalone: true });
const registry = new SourceRegistry(session);
const volume = { volumeId: "C:\\", label: "Fixture", fileSystem: "NTFS", volumeKind: "physical",
  totalBytes: 1000, freeBytes: 400, isReady: true, supportsMasterFileTable: true };
let bytes = 100;
const summary = () => ({ scope: "all", mode: "balanced", target: "", roots: ["C:\\", "D:\\", "E:\\"],
  scanKind: "fixture", skipped: [], completedAt: "2026-10-27T00:00:00Z", durationSeconds: 1,
  totalBytes: bytes, scannedBytes: bytes, fileCount: 1, directoryCount: 1, skippedCount: 0 });
let failVolumes = false, failResult = false;
let delayedRoot: number | null = null;
let releaseRead = () => {};
let pendingRead: Promise<void> | null = null;
const calls: string[] = [];
const signals: AbortSignal[] = [];
const requestClient = { request: async (request: { url: string; signal?: AbortSignal }) => {
  calls.push(request.url);
  if (request.signal) signals.push(request.signal);
  if (request.url.endsWith("/volumes")) {
    if (failVolumes) throw new Error("volumes failed");
    return [volume];
  }
  if (failResult) throw new Error("result failed");
  if (request.url.endsWith("/summary")) return summary();
  const query = new URL(request.url, "http://fixture").searchParams;
  if (Number(query.get("rootIndex")) === delayedRoot && pendingRead) await pendingRead;
  return diskTreeBuffer([{ id: 0, parent: -1, name: "root", directory: true, size: bytes },
    { id: 1, parent: 0, name: "file", directory: false, size: bytes }]);
} } as unknown as RequestClient;
let invalidation: ((value: OperationInvalidation) => void) | undefined;
let scanState = "succeeded";
const operations = { snapshot: { operations: [] } as unknown as OperationRegistrySnapshot,
  subscribe: () => () => {}, subscribeInvalidations: (listener: typeof invalidation) => { invalidation = listener; return () => { invalidation = undefined; }; },
  get: () => ({ state: scanState }), submit: async () => {} } as unknown as OperationRegistry;
const workers: { disposed: boolean; layouts: number }[] = [];
const owner = createDiskUsageOwner({ backendSession: session, sourceRegistry: registry, requestClient, operationRegistry: operations }, () => {
  const state = { disposed: false, layouts: 0 }; workers.push(state);
  let tree: ReturnType<typeof decodeDiskUsageTree> = null;
  return {
    replaceTree: async (buffer: ArrayBuffer) => { tree = decodeDiskUsageTree(buffer); return tree ? layoutDiskUsageTree(tree) : null; },
    layout: async (root: number, view?: DiskUsageViewWindow) => { state.layouts++; return tree ? layoutDiskUsageTree(tree, root, view) : null; },
    dispose: () => { state.disposed = true; }
  };
});
const tick = async () => { for (let i = 0; i < 100; i++) await Promise.resolve(); };
const invalidate = () => invalidation!({ kind: "disk-usage.scan", operationId: "scan", domainKey: null, targets: ["diskUsage"] });
try {
  assert.equal(owner.summary(), undefined);
  assert.equal(owner.layout(), undefined);
  assert.equal(calls.length, 0, "reading current values does not sample");
  owner.setActive(true); await tick();
  assert.equal(owner.summary()?.totalBytes, 100);
  assert.ok(owner.layout());
  assert.equal(owner.volumesReadState(), "ready");
  assert.equal(calls.filter(url => url.includes("/tree?")).length, 1);
  const accepted = owner.layout(), acceptedSummary = owner.summary(), reset = owner.resetNonce();
  owner.setSelected(1);
  owner.setActive(false);
  const afterHide = calls.length;
  await tick();
  assert.equal(calls.length, afterHide, "withdrawal performs no sampling");
  assert.equal(owner.layout(), accepted);
  owner.setActive(true);
  assert.equal(owner.layout(), accepted, "re-entry immediately exposes the accepted layout before any await");
  assert.equal(owner.summary(), acceptedSummary);
  assert.equal(owner.selected(), 1);
  assert.equal(owner.resetNonce(), reset, "visibility restoration does not reset the viewport");
  await tick();
  assert.equal(owner.layout(), accepted, "restoration does not rebuild the worker tree");
  assert.equal(workers.length, 1);
  failVolumes = true; await owner.readVolumes();
  assert.equal(owner.volumesReadState(), "error");
  assert.equal(owner.volumes()[0], volume, "existing disk failure contract retains stale volume facts");
  failVolumes = false;
  failResult = true; owner.selectRoot(1); await tick();
  assert.equal(owner.layout(), accepted);
  assert.equal(owner.selectedRootIndex(), 0);
  assert.equal(owner.resultReadFailure()?.kind, "result");
  failResult = false; owner.resultReadFailure()!.retry(); await tick();
  assert.equal(owner.selectedRootIndex(), 1, "retry accepts the originally requested root");
  assert.equal(owner.resultReadFailure(), null);
  assert.equal(workers[0].disposed, true, "replacement releases the old worker");

  delayedRoot = 2;
  pendingRead = new Promise<void>(resolve => { releaseRead = resolve; });
  owner.selectRoot(2); await tick();
  owner.selectRoot(0); await tick();
  const newer = owner.layout();
  assert.equal(owner.selectedRootIndex(), 0);
  releaseRead(); pendingRead = null; await tick();
  assert.equal(owner.layout(), newer, "late query cannot replace the newer root");
  assert.equal(owner.selectedRootIndex(), 0);

  owner.setActive(false);
  bytes = 200;
  const beforeInvalidation = calls.length;
  invalidate(); await tick();
  assert.equal(calls.length, beforeInvalidation, "hidden command invalidation defers sampling");
  assert.equal(owner.layout(), newer);
  owner.setActive(true);
  assert.equal(owner.layout(), newer, "invalidated re-entry still renders current facts first");
  await tick();
  assert.equal(owner.summary()?.totalBytes, 200);
  assert.notEqual(owner.layout(), newer);
  delayedRoot = 0;
  pendingRead = new Promise<void>(resolve => { releaseRead = resolve; });
  bytes = 300;
  invalidate(); await tick();
  const beforeInterruptedInvalidation = owner.layout();
  owner.setActive(false);
  releaseRead(); pendingRead = null; await tick();
  delayedRoot = null;
  owner.setActive(true);
  assert.equal(owner.layout(), beforeInterruptedInvalidation);
  await tick();
  assert.equal(owner.summary()?.totalBytes, 300, "an interrupted invalidation remains pending until replacement is accepted");
  scanState = "failed"; invalidate();
  assert.equal(owner.scanFailed(), true);

  delayedRoot = 2;
  pendingRead = new Promise<void>(resolve => { releaseRead = resolve; });
  owner.selectRoot(2); await tick();
  const beforeCancel = owner.layout();
  owner.setActive(false);
  assert.ok(signals.some(signal => signal.aborted), "withdrawal cancels safe reads");
  releaseRead(); pendingRead = null; await tick();
  assert.equal(owner.layout(), beforeCancel);
  assert.equal(owner.queryBusy(), false);
} finally {
  owner.dispose(); registry.dispose(); session.dispose();
}
assert.ok(workers.every(worker => worker.disposed), "runtime disposal releases every accepted/staged worker");
console.log("diskUsageOwner: synchronous current values, visibility, failure/retry, late query, deferred invalidation and cleanup passed");
