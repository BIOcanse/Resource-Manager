import { createEffect, createMemo, createSignal, onCleanup, onMount, Show } from "solid-js";
import { openPath, openProperties } from "../../api";
import { diskUsageScanCommand } from "../../data/operations/operationCommands.ts";
import { isTerminalOperation } from "../../frontendRuntime/operations/operationMerge.ts";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext.tsx";
import { DiskUsageContextMenu } from "./DiskUsageContextMenu";
import { DiskUsagePage } from "./DiskUsagePage";
import { DiskUsageResultPanel } from "./DiskUsageResultPanel";
import { tileFactsOf } from "./diskUsageLayoutFacts.ts";
import { DiskUsageLayoutWorker } from "./diskUsageLayoutWorker.ts";
import { needsMoreDetail } from "./diskUsageViewport.ts";
import type { DiskUsageViewWindow } from "./diskUsageViewport.ts";
import {
  getDiskUsageTree,
  getDiskUsageNode,
  getDiskUsageSummary,
  getDiskUsageVolumes
} from "./diskUsageApi.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import type {
  DiskUsageLayout,
  DiskUsageNode,
  DiskUsageScanSummary
} from "./diskUsageLayoutTypes.ts";
import type {
  DiskUsageScanMode,
  DiskUsageScanScope,
  DiskUsageVolume
} from "./diskUsageTypes.ts";
import { uiText } from "../../text.ts";

/**
 * 磁盘占用页的外壳。
 *
 * 它是这一页的状态所有者：卷清单、扫描进行中与否、当前布局、选中项、右键菜单。
 *
 * 后端只发布扫描事实。事实缓冲区交给本页的 Worker，布局、剪枝、缩放和钻取都留在前端。
 * 画图与悬停纯查表；右键菜单另读单节点的分配字节等事实。
 */
export function DiskUsageWorkspace() {
  const runtime = useFrontendRuntime();
  const frontendWork = useFrontendWork();
  const [volumes, setVolumes] = createSignal<DiskUsageVolume[]>([]);
  const [operationSnapshot, setOperationSnapshot] =
    createSignal(runtime.operationRegistry.snapshot);
  const scanning = createMemo(() => operationSnapshot().operations.some((operation) =>
    operation.kind === "disk-usage.scan" && !isTerminalOperation(operation)));
  const [volumesReadState, setVolumesReadState] = createSignal<"loading" | "ready" | "error">("loading");
  const [resultReadFailure, setResultReadFailure] = createSignal<{ kind: "result" | "layout"; retry: () => void } | null>(null);
  const [scanFailed, setScanFailed] = createSignal(false);
  const [refreshOnResume, setRefreshOnResume] = createSignal(false);
  const [summary, setSummary] = createSignal<DiskUsageScanSummary | null | undefined>(undefined);
  const [layout, setLayout] = createSignal<DiskUsageLayout | null | undefined>(undefined);
  const [selectedRootIndex, setSelectedRootIndex] = createSignal(0);
  const [selected, setSelected] = createSignal(-1);
  const [menu, setMenu] = createSignal<
    { node: DiskUsageNode; x: number; y: number } | null>(null);
  const [drillStack, setDrillStack] = createSignal<number[]>([]);
  const [queryBusy, setQueryBusy] = createSignal(false);
  const [queryCursors, setQueryCursors] = createSignal<(number | undefined)[]>([undefined]);
  const [queryPage, setQueryPage] = createSignal(0);
  let workspaceController: AbortController | null = null;
  let workspaceGeneration = 0;
  let layoutToken = 0;
  let volumesToken = 0;
  let layoutWorker: DiskUsageLayoutWorker | null = null;
  let requestedLayoutRoot: number | null = null;

  function cancelled(cause: unknown, signal?: AbortSignal) {
    return signal?.aborted || (cause instanceof Error && cause.name === "AbortError");
  }

  async function readVolumes() {
    const signal = workspaceController?.signal;
    const generation = workspaceGeneration;
    const token = ++volumesToken;
    try {
      const value = await getDiskUsageVolumes(runtime.requestClient, signal);
      if (token !== volumesToken || generation !== workspaceGeneration || signal?.aborted) return;
      setVolumes(value);
      setVolumesReadState("ready");
    } catch (cause) {
      if (token !== volumesToken || generation !== workspaceGeneration || cancelled(cause, signal)) return;
      setVolumesReadState("error");
    }
  }

  onMount(() => {
    layoutWorker = new DiskUsageLayoutWorker();
    const controller = new AbortController();
    workspaceGeneration++;
    workspaceController = controller;
    void readVolumes();
    void refreshResult(controller.signal);

    const unsubscribeOperations = runtime.operationRegistry.subscribe(setOperationSnapshot);
    const unsubscribeInvalidations = runtime.operationRegistry.subscribeInvalidations((invalidation) => {
      if (invalidation.kind !== "disk-usage.scan"
          || !invalidation.targets.includes("diskUsage")) {
        return;
      }
      const operation = runtime.operationRegistry.get(invalidation.operationId);
      if (operation?.state !== "succeeded") {
        setScanFailed(true);
        return;
      }
      setScanFailed(false);
      setDrillStack([]);
      if (frontendWork.frontendVisible()) {
        void refreshResult(controller.signal, undefined, 0);
      } else {
        setRefreshOnResume(true);
      }
    });

    // 盘符是会变的：U 盘插上、BitLocker 解锁、虚拟盘挂载，随时会多一个。
    // 每次打开这一页都会重读（这个组件是随页面挂载的），
    // 但页面一直开着的时候也得能发现 —— 窗口重新拿到焦点时再读一次。
    const refreshVolumes = () => void readVolumes();
    window.addEventListener("focus", refreshVolumes);

    onCleanup(() => {
      workspaceGeneration++;
      layoutToken++;
      workspaceController = null;
      unsubscribeInvalidations();
      unsubscribeOperations();
      window.removeEventListener("focus", refreshVolumes);
      controller.abort();
      layoutWorker?.dispose();
      layoutWorker = null;
    });
  });

  let wasFrontendVisible = frontendWork.frontendVisible();
  createEffect(() => {
    const visible = frontendWork.frontendVisible();
    if (visible && !wasFrontendVisible && refreshOnResume()) {
      setRefreshOnResume(false);
      void refreshResult(workspaceController?.signal, undefined, 0);
      void readVolumes();
    }
    wasFrontendVisible = visible;
  });

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
        // 布局与视口都由本页 Worker 计算，不发给后端。
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
    page = 0
  ) {
    const generation = workspaceGeneration;
    const token = ++layoutToken;
    setQueryBusy(true);
    try {
      const [nextSummary, treeBuffer] = await Promise.all([
        getDiskUsageSummary(runtime.requestClient, signal),
        getDiskUsageTree(runtime.requestClient, signal, rootIndex, nodeId, cursor)
      ]);
      if (token !== layoutToken
          || generation !== workspaceGeneration
          || signal?.aborted) return;
      const nextLayout = await layoutWorker!.replaceTree(treeBuffer);
      if (token !== layoutToken || generation !== workspaceGeneration || signal?.aborted) return;
      // 换了根就不能再拿旧根的视图做比较，清掉让它重新细化一次。
      shownView = null;
      setSummary(nextSummary);
      setLayout(nextLayout);
      if (nextDrillStack) setDrillStack(nextDrillStack);
      if (rootIndex !== selectedRootIndex()) setDrillStack([]);
      setQueryCursors(cursors);
      setQueryPage(page);
      setSelectedRootIndex(rootIndex);
      setSelected(-1);
      setResultReadFailure(null);
      setResetNonce((value) => value + 1);
    } catch (cause) {
      if (token !== layoutToken || generation !== workspaceGeneration || cancelled(cause, signal)) return;
      setResultReadFailure({ kind: "result", retry: () => void refreshResult(workspaceController?.signal, nodeId, rootIndex, cursor, nextDrillStack, cursors, page) });
    } finally {
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
    setMenu(null);
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

  return (
    <>
      <DiskUsagePage
        volumes={volumes()}
        scanning={scanning()}
        volumesReadState={volumesReadState()}
        hasResult={Boolean(layout() && summary())}
        resultReadState={resultReadFailure() ? "error"
          : summary() === undefined || layout() === undefined ? "loading" : "ready"}
        onRetryVolumes={() => void readVolumes()}
        onScan={(scope, mode, target) => void startScan(scope, mode, target)}
      >
        <Show when={scanFailed()}>
          <p role="alert">{uiText.diskUsage.scanFailed}</p>
        </Show>
        <Show when={resultReadFailure()}>
          {(failure) => <div class="disk-usage-read-error" role="alert">
            <p>{uiText.diskUsage.resultReadFailed}</p>
            <Show when={layout() && summary()}><p>{uiText.diskUsage.retainedResultStale}</p></Show>
            <button type="button" onClick={() => failure().retry()}>{uiText.diskUsage.retryRead}</button>
          </div>}
        </Show>
        <Show when={layout() && summary()}>
          <DiskUsageResultPanel
            summary={summary()!}
            layout={layout()!}
            selectedRootIndex={selectedRootIndex()}
            onSelectRoot={selectRoot}
            selectedNodeId={selected()}
            factsOf={factsOf}
            labelOf={labelOf}
            onSelect={setSelected}
            onActivate={(nodeId) => {
              if (factsOf(nodeId)?.isDirectory) {
                drillInto(nodeId);
              }
            }}
            onContextMenu={(nodeId, x, y) => {
              // 右键菜单还需要分配字节等单节点事实。
              void getDiskUsageNode(runtime.requestClient, nodeId)
                .then((node) => setMenu({ node, x, y }))
                .catch(() => undefined);
            }}
            onViewChanged={onViewChanged}
            onResetView={resetView}
            resetNonce={resetNonce()}
            onNavigateUp={navigateUp}
            canNavigateUp={drillStack().length > 0}
            queryPageNumber={layout()!.rootNodeId === layout()!.queryRootNodeId ? queryPage() + 1 : 1}
            queryPartial={layout()!.subtreeComplete[0] === 0}
            hasNextPage={layout()!.rootNodeId === layout()!.queryRootNodeId && layout()!.nextCursorNodeId >= 0}
            hasPreviousPage={layout()!.rootNodeId === layout()!.queryRootNodeId && queryPage() > 0}
            queryBusy={queryBusy()}
            onNextPage={() => changeQueryPage(true)}
            onPreviousPage={() => changeQueryPage(false)}
          />
        </Show>
      </DiskUsagePage>

      <Show when={menu()}>
        {(current) => (
          <DiskUsageContextMenu
            node={current().node}
            clientX={current().x}
            clientY={current().y}
            onClose={() => setMenu(null)}
            onDrillDown={() => {
              const nodeId = current().node.nodeId;
              setMenu(null);
              drillInto(nodeId);
            }}
            onOpenLocation={() => {
              const path = current().node.fullPath;
              setMenu(null);
              void openPath(path, true);
            }}
            onProperties={() => {
              const path = current().node.fullPath;
              setMenu(null);
              void openProperties(path);
            }}
            onCopyPath={() => {
              const path = current().node.fullPath;
              setMenu(null);
              void navigator.clipboard?.writeText(path);
            }}
          />
        )}
      </Show>
    </>
  );
}
