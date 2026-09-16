import { createSignal, onCleanup, onMount, Show } from "solid-js";
import { openPath, openProperties } from "../api";
import { diskUsageScanCommand } from "../data/operations/operationCommands.ts";
import { DiskUsageContextMenu } from "../diskUsage/DiskUsageContextMenu";
import { DiskUsagePage } from "../diskUsage/DiskUsagePage";
import { DiskUsageResultPanel } from "../diskUsage/DiskUsageResultPanel";
import { tileFactsOf } from "../diskUsage/diskUsageLayoutFacts.ts";
import { needsMoreDetail } from "../diskUsage/diskUsageViewport.ts";
import type { DiskUsageViewWindow } from "../diskUsage/diskUsageViewport.ts";
import {
  getDiskUsageLayout,
  getDiskUsageNode,
  getDiskUsageSummary,
  getDiskUsageVolumes
} from "../diskUsage/diskUsageApi.ts";
import { useFrontendRuntime } from "../frontendRuntime/FrontendRuntimeContext";
import type {
  DiskUsageLayout,
  DiskUsageNode,
  DiskUsageScanSummary
} from "../diskUsage/diskUsageLayoutTypes.ts";
import type {
  DiskUsageScanMode,
  DiskUsageScanScope,
  DiskUsageVolume
} from "../diskUsage/diskUsageTypes.ts";

/**
 * 磁盘占用页的外壳。
 *
 * 它是这一页的状态所有者：卷清单、扫描进行中与否、当前布局、选中项、右键菜单。
 *
 * 画图和悬停要的事实全部来自布局本身，不按需去后端取。
 * 先前按需取的写法有个环：取名字既读又写同一个信号，而读发生在绘制里，
 * 于是每取回一个名字就重画一遍几万个方格，重画又发起更多请求，界面直接卡死。
 * 只有右键菜单还去取一次单节点 —— 它要的 fileCount 不在布局里，
 * 而且那是用户点出来的一次请求，不在热路径上。
 */
export function DiskUsageWorkspace() {
  const runtime = useFrontendRuntime();
  const [volumes, setVolumes] = createSignal<DiskUsageVolume[]>([]);
  const [scanning, setScanning] = createSignal(false);
  const [summary, setSummary] = createSignal<DiskUsageScanSummary | null>(null);
  const [layout, setLayout] = createSignal<DiskUsageLayout | null>(null);
  const [selected, setSelected] = createSignal(-1);
  const [menu, setMenu] = createSignal<
    { node: DiskUsageNode; x: number; y: number } | null>(null);
  const [drillStack, setDrillStack] = createSignal<number[]>([]);


  onMount(() => {
    const controller = new AbortController();
    void getDiskUsageVolumes(runtime.requestClient, controller.signal)
      .then(setVolumes)
      .catch(() => setVolumes([]));
    void refreshResult(controller.signal);
    onCleanup(() => controller.abort());
  });

  // 当前这一份布局是按哪个视图算出来的。用来判断新视图值不值得再要一份。
  let shownView: DiskUsageViewWindow | null = null;
  // 每次换布局都加一号，回来的响应对不上号就丢掉 —— 慢的那个不能盖住快的那个。
  let layoutToken = 0;

  /**
   * 视图变了：如果现在能看见的细节比手上这份布局多，就换一份。
   *
   * 判断很简单：放大了（更多方格够得上像素门槛），或者视野移出了原来那块。
   * 缩小回去不用换 —— 手上这份已经覆盖了更小的范围，够画。
   */
  function onViewChanged(view: DiskUsageViewWindow) {
    const current = layout();
    if (!current) {
      return;
    }
    if (shownView && !needsMoreDetail(shownView, view)) {
      return;
    }
    const token = ++layoutToken;
    const rootNodeId = current.rootNodeId;
    void getDiskUsageLayout(runtime.requestClient, rootNodeId, undefined, view)
      .then((next) => {
        if (token !== layoutToken || !next || next.rootNodeId !== rootNodeId) {
          return;
        }
        shownView = view;
        setLayout(next);
      })
      .catch(() => undefined);
  }

  async function refreshResult(signal?: AbortSignal, nodeId?: number) {
    try {
      const [nextSummary, nextLayout] = await Promise.all([
        getDiskUsageSummary(runtime.requestClient, signal),
        getDiskUsageLayout(runtime.requestClient, nodeId, signal)
      ]);
      // 换了根就不能再拿旧根的视图做比较，清掉让它重新细化一次。
      shownView = null;
      layoutToken++;
      setSummary(nextSummary);
      setLayout(nextLayout);
      setSelected(-1);
    } catch {
      // 还没扫过或请求被取消：保持空态，不弹错误。
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
    setScanning(true);
    try {
      const operation = await runtime.operationRegistry.submit(
        diskUsageScanCommand({ scope, mode, target }));
      await runtime.operationRegistry.waitForTerminal(operation.id);
      setDrillStack([]);
      await refreshResult();
    } catch {
      // 失败的原因已经进了任务中心，这里不再重复弹一次。
    } finally {
      setScanning(false);
    }
  }

  function drillInto(nodeId: number) {
    const current = layout();
    if (!current) {
      return;
    }
    setDrillStack((stack) => [...stack, current.rootNodeId]);
    void refreshResult(undefined, nodeId);
  }

  function navigateUp() {
    const stack = drillStack();
    if (stack.length === 0) {
      return;
    }
    const parent = stack[stack.length - 1];
    setDrillStack(stack.slice(0, -1));
    void refreshResult(undefined, parent);
  }

  return (
    <>
      <DiskUsagePage
        volumes={volumes()}
        scanning={scanning()}
        onScan={(scope, mode, target) => void startScan(scope, mode, target)}
      >
        <Show when={layout() && summary()}>
          <DiskUsageResultPanel
            summary={summary()!}
            layout={layout()!}
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
              // 右键菜单要的 fileCount 不在布局里，这里才去取一次。
              void getDiskUsageNode(runtime.requestClient, nodeId)
                .then((node) => setMenu({ node, x, y }))
                .catch(() => undefined);
            }}
            onViewChanged={onViewChanged}
            onNavigateUp={navigateUp}
            canNavigateUp={drillStack().length > 0}
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
