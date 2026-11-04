import { batch, createSignal } from "solid-js";
import type { FrontendRuntime } from "../../frontendRuntime/FrontendRuntime.ts";
import { diskUsageScanCommand } from "../../data/operations/operationCommands.ts";
import { isTerminalOperation } from "../../frontendRuntime/operations/operationMerge.ts";
import { getDiskUsageSummary, getDiskUsageTree, getDiskUsageVolumes } from "./diskUsageApi.ts";
import { DiskUsageLayoutWorker } from "./diskUsageLayoutWorker.ts";
import { tileFactsOf } from "./diskUsageLayoutFacts.ts";
import { needsMoreDetail, type DiskUsageViewWindow } from "./diskUsageViewport.ts";
import type { DiskUsageLayout, DiskUsageScanSummary } from "./diskUsageLayoutTypes.ts";
import type { DiskUsageScanMode, DiskUsageScanScope, DiskUsageVolume } from "./diskUsageTypes.ts";

type DiskLayoutWorker = Pick<DiskUsageLayoutWorker, "replaceTree" | "layout" | "dispose">;
type DiskUsageOwnerRuntime = Pick<FrontendRuntime, "requestClient" | "sourceRegistry" | "operationRegistry" | "backendSession">;

/** Runtime owns accepted disk facts/layout; page and window demand only govern new work. */
export function createDiskUsageOwner(
  runtime: DiskUsageOwnerRuntime,
  makeWorker: () => DiskLayoutWorker = () => new DiskUsageLayoutWorker()
) {
  const volumeSource = runtime.sourceRegistry.define({
    key: "disk-usage.volumes",
    load: signal => getDiskUsageVolumes(runtime.requestClient, signal),
    retainLastGood: false
  });
  const resultSource = runtime.sourceRegistry.defineFamily({
    key: "disk-usage.result",
    load: async (query: { rootIndex: number; nodeId: number | null; cursor: number | null }, signal) => {
      const [summary, treeBuffer] = await Promise.all([
        getDiskUsageSummary(runtime.requestClient, signal),
        getDiskUsageTree(runtime.requestClient, signal, query.rootIndex, query.nodeId ?? undefined, query.cursor ?? undefined)
      ]);
      if (signal.aborted) throw new DOMException("Disk result cancelled", "AbortError");
      return { summary, treeBuffer };
    },
    retainLastGood: false
  });
  const [volumes, setVolumes] = createSignal<DiskUsageVolume[]>([]);
  const [operationSnapshot, setOperationSnapshot] =
    createSignal(runtime.operationRegistry.snapshot);
  const scanning = () => operationSnapshot().operations.some((operation) =>
    operation.kind === "disk-usage.scan" && !isTerminalOperation(operation));
  const [volumesReadState, setVolumesReadState] = createSignal<"loading" | "ready" | "error">("loading");
  const [resultReadFailure, setResultReadFailure] = createSignal<{ kind: "result" | "layout"; retry: () => void } | null>(null);
  const [scanFailed, setScanFailed] = createSignal(false);
  const [refreshOnResume, setRefreshOnResume] = createSignal(false);
  const [summary, setSummary] = createSignal<DiskUsageScanSummary | null | undefined>(undefined);
  const [layout, setLayout] = createSignal<DiskUsageLayout | null | undefined>(undefined);
  const [selectedRootIndex, setSelectedRootIndex] = createSignal(0);
  const [selected, setSelected] = createSignal(-1);
  const [drillStack, setDrillStack] = createSignal<number[]>([]);
  const [queryBusy, setQueryBusy] = createSignal(false);
  const [queryCursors, setQueryCursors] = createSignal<(number | undefined)[]>([undefined]);
  const [queryPage, setQueryPage] = createSignal(0);
  let demanded = false;
  let active = false;
  let disposed = false;
  let workspaceController: AbortController | null = null;
  let workspaceGeneration = 0;
  let layoutToken = 0;
  let volumesToken = 0;
  let layoutWorker: DiskLayoutWorker | null = null;
  let requestedLayoutRoot: number | null = null;

  function cancelled(cause: unknown, signal?: AbortSignal) {
    return signal?.aborted || (cause instanceof Error && cause.name === "AbortError");
  }

  async function readVolumes() {
    if (!active) return;
    const signal = workspaceController?.signal;
    const generation = workspaceGeneration;
    const token = ++volumesToken;
    const lease = volumeSource.acquire({ active: false });
    const release = () => lease.release();
    signal?.addEventListener("abort", release, { once: true });
    try {
      const reading = lease.refresh();
      lease.setDemand({ active: true });
      const result = await reading;
      if (result.status !== "ready" || result.data === null) throw result.error;
      const value = result.data;
      if (token !== volumesToken || generation !== workspaceGeneration || signal?.aborted) return;
      batch(() => {
        setVolumes(value);
        setVolumesReadState("ready");
      });
    } catch (cause) {
      if (token !== volumesToken || generation !== workspaceGeneration || cancelled(cause, signal)) return;
      setVolumesReadState("error");
    } finally {
      signal?.removeEventListener("abort", release);
      release();
    }
  }

  const unsubscribeOperations = runtime.operationRegistry.subscribe(setOperationSnapshot);
  const unsubscribeInvalidations = runtime.operationRegistry.subscribeInvalidations((invalidation) => {
    if (invalidation.kind !== "disk-usage.scan" || !invalidation.targets.includes("diskUsage")) return;
    const operation = runtime.operationRegistry.get(invalidation.operationId);
    if (operation?.state !== "succeeded") { setScanFailed(true); return; }
    setScanFailed(false);
    setDrillStack([]);
    setRefreshOnResume(true);
    if (active) void refreshResult(workspaceController?.signal, undefined, 0, undefined, undefined, [undefined], 0, true);
  });

  function setActive(next: boolean) {
    demanded = next;
    updateActive(next);
  }

  function updateActive(next: boolean) {
    if (disposed || next === active) return;
    active = next;
    if (!next) {
      workspaceGeneration++;
      layoutToken++;
      volumesToken++;
      workspaceController?.abort();
      workspaceController = null;
      setQueryBusy(false);
      requestedLayoutRoot = null;
      return;
    }
    workspaceController = new AbortController();
    void readVolumes();
    if (summary() === undefined || refreshOnResume()) {
      void refreshResult(workspaceController.signal, undefined, 0);
    }
  }

  function dispose() {
    if (disposed) return;
    setActive(false);
    disposed = true;
    unsubscribeInvalidations();
    unsubscribeOperations();
    unsubscribeSession();
    layoutWorker?.dispose();
    layoutWorker = null;
  }

  // View associated with the accepted layout, used to decide refinement.
  let shownView: DiskUsageViewWindow | null = null;
  // Increment layout generation so stale responses cannot overwrite newer results.
  // Changing this value resets the viewport to the full map.
  const [resetNonce, setResetNonce] = createSignal(0);

  /** Refine when zoom or newly visible bounds expose more detail; covered views need no new layout. */
  function onViewChanged(view: DiskUsageViewWindow) {
    const current = layout();
    if (!current || resultReadFailure() || queryBusy()
        || (requestedLayoutRoot !== null && requestedLayoutRoot !== current.rootNodeId)) {
      return;
    }
    // An off-map viewport has no visible area to refine.
    if (view.maxX <= view.minX || view.maxY <= view.minY) {
      return;
    }
    if (shownView && !needsMoreDetail(shownView, view)) {
      return;
    }
    requestLayout(current.rootNodeId, view);
  }

  /** Compute layout locally and reset the canvas only after drilling completes. */
  function requestLayout(rootNodeId: number, view?: DiskUsageViewWindow, reset = false, nextDrillStack?: number[]) {
    // An incomplete branch needs a neutral subtree query; view refinement stays local.
    if (reset && factsOf(rootNodeId)?.subtreeComplete === false) {
      void refreshResult(workspaceController?.signal, rootNodeId, selectedRootIndex(), undefined, nextDrillStack);
      return;
    }
    if (!active) return;
    const token = ++layoutToken;
    requestedLayoutRoot = rootNodeId;
    const signal = workspaceController?.signal;
    void layoutWorker!.layout(rootNodeId, view)
      .then((next) => {
        if (token !== layoutToken || signal?.aborted) return;
        if (next && "queryNodeId" in next) {
          void refreshResult(signal, next.queryNodeId, selectedRootIndex(), undefined, nextDrillStack);
          return;
        }
        if (token !== layoutToken || signal?.aborted || (next && next.rootNodeId !== rootNodeId)) {
          return;
        }
        // Compute layout and viewport changes in the disk domain's background thread.
        shownView = next?.view ?? null;
        setLayout(next);
        if (nextDrillStack) setDrillStack(nextDrillStack);
        if (reset) setResetNonce((value) => value + 1);
        setResultReadFailure((failure) => failure?.kind === "result" ? failure : null);
      })
      .catch((cause: unknown) => {
        if (token !== layoutToken || cancelled(cause, signal)) return;
        setResultReadFailure((failure) => failure?.kind === "result" ? failure
          : { kind: "layout", retry: () => requestLayout(rootNodeId, view, reset, nextDrillStack) });
      })
      .finally(() => {
        if (token === layoutToken) requestedLayoutRoot = null;
      });
  }

  /** Reset both viewport and layout coverage so the full map remains renderable. */
  function resetView() {
    const current = layout();
    if (!current) {
      return;
    }
    if (queryBusy() || (requestedLayoutRoot !== null && requestedLayoutRoot !== current.rootNodeId)) {
      setResetNonce((value) => value + 1);
      return;
    }
    shownView = null;
    requestLayout(current.rootNodeId);
    setResetNonce((value) => value + 1);
  }

  async function refreshResult(
    signal: AbortSignal | undefined = workspaceController?.signal,
    nodeId?: number,
    rootIndex = selectedRootIndex(),
    cursor?: number,
    nextDrillStack?: number[],
    cursors: (number | undefined)[] = [undefined],
    page = 0,
    afterCurrent = false
  ) {
    if (!active) return;
    const generation = workspaceGeneration;
    const token = ++layoutToken;
    setQueryBusy(true);
    const lease = resultSource.acquire({ rootIndex, nodeId: nodeId ?? null, cursor: cursor ?? null }, { active: false });
    const release = () => lease.release();
    signal?.addEventListener("abort", release, { once: true });
    let candidate: DiskLayoutWorker | null = null;
    const cancelLayout = () => candidate?.dispose();
    signal?.addEventListener("abort", cancelLayout, { once: true });
    try {
      const reading = lease.refresh({ afterCurrent });
      lease.setDemand({ active: true });
      const result = await reading;
      if (result.status !== "ready" || result.data === null) throw result.error;
      if (token !== layoutToken || generation !== workspaceGeneration || signal?.aborted) return;
      const { summary: nextSummary, treeBuffer } = result.data;
      // Stage the replacement so cancellation cannot destroy the accepted view's tree.
      candidate = makeWorker();
      const nextLayout = await candidate.replaceTree(treeBuffer.slice(0));
      if (token !== layoutToken
          || generation !== workspaceGeneration
          || signal?.aborted) {
        return;
      }
      layoutWorker?.dispose();
      layoutWorker = candidate;
      candidate = null;
      // A new root invalidates the old view comparison and requires refinement.
      shownView = null;
      batch(() => {
        setSummary(nextSummary);
        setLayout(nextLayout);
        if (nextDrillStack) setDrillStack(nextDrillStack);
        if (rootIndex !== selectedRootIndex()) setDrillStack([]);
        setQueryCursors(cursors);
        setQueryPage(page);
        setSelectedRootIndex(rootIndex);
        setSelected(-1);
        setResultReadFailure(null);
        setRefreshOnResume(false);
        setResetNonce((value) => value + 1);
      });
    } catch (cause) {
      if (token !== layoutToken || generation !== workspaceGeneration || cancelled(cause, signal)) return;
      setResultReadFailure({ kind: "result", retry: () => void refreshResult(workspaceController?.signal, nodeId, rootIndex, cursor, nextDrillStack, cursors, page, afterCurrent) });
    } finally {
      signal?.removeEventListener("abort", release);
      signal?.removeEventListener("abort", cancelLayout);
      candidate?.dispose();
      release();
      if (token === layoutToken) {
        setQueryBusy(false);
        requestedLayoutRoot = null;
      }
    }
  }

  /** Resolve tile facts directly from the accepted layout without a request. */
  function factsOf(nodeId: number) {
    const current = layout();
    return current && nodeId >= 0 ? tileFactsOf(current, nodeId) : undefined;
  }

  /** Label lookup is a render hot path and must not request data or write signals. */
  function labelOf(nodeId: number) {
    const current = layout();
    if (!current) {
      return "";
    }
    const index = current.indexByNodeId.get(nodeId);
    return index === undefined ? "" : current.names[index];
  }

  async function startScan(
    scope: DiskUsageScanScope,
    mode: DiskUsageScanMode,
    target: string
  ) {
    setScanFailed(false);
    try {
      await runtime.operationRegistry.submit(diskUsageScanCommand({ scope, mode, target }));
    } catch {
      setScanFailed(true);
    }
  }

  function drillInto(nodeId: number) {
    const current = layout();
    if (!current || queryBusy() || (requestedLayoutRoot !== null && requestedLayoutRoot !== current.rootNodeId)) {
      return;
    }
    shownView = null;
    setSelected(-1);
    requestLayout(nodeId, undefined, true, [...drillStack(), current.rootNodeId]);
  }

  function navigateUp() {
    const stack = drillStack();
    const current = layout();
    if (stack.length === 0 || queryBusy() || !current
        || (requestedLayoutRoot !== null && requestedLayoutRoot !== current.rootNodeId)) {
      return;
    }
    const parent = stack[stack.length - 1];
    shownView = null;
    setSelected(-1);
    requestLayout(parent, undefined, true, stack.slice(0, -1));
  }

  function selectRoot(rootIndex: number) {
    const roots = summary()?.roots;
    if (!roots || rootIndex < 0 || rootIndex >= roots.length || rootIndex === selectedRootIndex()) {
      return;
    }
    void refreshResult(undefined, undefined, rootIndex);
  }

  function changeQueryPage(forward: boolean) {
    const current = layout();
    if (!current || queryBusy() || current.rootNodeId !== current.queryRootNodeId) return;
    const page = queryPage() + (forward ? 1 : -1);
    if (page < 0 || (forward && current.nextCursorNodeId < 0)) return;
    const cursors = forward
      ? [...queryCursors().slice(0, page), current.nextCursorNodeId]
      : queryCursors();
    void refreshResult(workspaceController?.signal, current.queryRootNodeId, selectedRootIndex(),
      cursors[page], drillStack(), cursors, page);
  }

  let backendEpoch = runtime.backendSession.snapshot.session?.epoch ?? null;
  const unsubscribeSession = runtime.backendSession.subscribe((snapshot) => {
    const epoch = snapshot.session?.epoch ?? null;
    if (epoch === backendEpoch) return;
    backendEpoch = epoch;
    updateActive(false);
    layoutWorker?.dispose();
    layoutWorker = null;
    batch(() => {
      setVolumes([]);
      setVolumesReadState("loading");
      setSummary(undefined);
      setLayout(undefined);
      setResultReadFailure(null);
      setRefreshOnResume(false);
      setSelectedRootIndex(0);
      setDrillStack([]);
      setQueryCursors([undefined]);
      setQueryPage(0);
      setSelected(-1);
    });
    if (demanded && snapshot.status === "ready") updateActive(true);
  });

  return {
    volumes, volumesReadState, scanning, scanFailed, summary, layout, resultReadFailure,
    selectedRootIndex, selected, setSelected, drillStack, queryBusy, queryPage, resetNonce,
    readVolumes, startScan, factsOf, labelOf, onViewChanged, resetView, drillInto,
    navigateUp, selectRoot, changeQueryPage, setActive, dispose
  };
}

export type DiskUsageOwner = ReturnType<typeof createDiskUsageOwner>;
