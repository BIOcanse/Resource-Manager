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

  // 当前布局对应的前端视图，决定是否需要 Worker 细化。
  let shownView: DiskUsageViewWindow | null = null;
  // 每次换布局都加一号，回来的响应对不上号就丢掉 —— 慢的那个不能盖住快的那个。
  // 这个数一变，方格图就把视口拨回整图。
  const [resetNonce, setResetNonce] = createSignal(0);

  /**
   * 视图变了：如果现在能看见的细节比手上这份布局多，就换一份。
   *
   * 判断很简单：放大了（更多方格够得上像素门槛），或者视野移出了原来那块。
   * 缩小回去不用换 —— 手上这份已经覆盖了更小的范围，够画。
   */
  function onViewChanged(view: DiskUsageViewWindow) {
    const current = layout();
    if (!current || resultReadFailure() || queryBusy()
        || (requestedLayoutRoot !== null && requestedLayoutRoot !== current.rootNodeId)) {
      return;
    }
    // 视图被推出图外时看得见的那块是空的，不需要细化。
    if (view.maxX <= view.minX || view.maxY <= view.minY) {
      return;
    }
    if (shownView && !needsMoreDetail(shownView, view)) {
      return;
    }
    requestLayout(current.rootNodeId, view);
  }

  /** 在本地 Worker 计算布局；钻取完成后才复位画布。 */
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
        // 布局与视口由领域 Worker 计算，不发给后端。
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

  /**
   * 复位：回到整图。
   *
   * 光把视口拨回去不够 —— 手上这份布局可能只覆盖放大后那一小块，
   * 视口回到整图之后那张位图只占屏幕上一丁点，看着就是一片空白。
   * 所以数据也要跟着回到整图。
   */
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
      // 换了根就不能再拿旧根的视图做比较，清掉让它重新细化一次。
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

  /** 方格的事实：名字、路径、大小、是不是目录。全部来自当前布局，不发请求。 */
  function factsOf(nodeId: number) {
    const current = layout();
    return current && nodeId >= 0 ? tileFactsOf(current, nodeId) : undefined;
  }

  /** 画名字用的纯查表。绘制是热路径，这里绝不能有请求或写信号。 */
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
