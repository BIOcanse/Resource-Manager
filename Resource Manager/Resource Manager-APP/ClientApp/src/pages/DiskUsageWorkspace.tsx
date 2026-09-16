import { createSignal, onCleanup, onMount, Show } from "solid-js";
import { openPath, openProperties } from "../api";
import { diskUsageScanCommand } from "../data/operations/operationCommands.ts";
import { DiskUsageContextMenu } from "../diskUsage/DiskUsageContextMenu";
import { DiskUsagePage } from "../diskUsage/DiskUsagePage";
import { DiskUsageResultPanel } from "../diskUsage/DiskUsageResultPanel";
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
 * 节点事实按需取并缓存 —— 名字和路径不随布局一起发，否则一次几万个方格就太重了。
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

  // 节点事实按需取；同一个节点只取一次。
  const nodeCache = new Map<number, DiskUsageNode>();
  const [nodeVersion, setNodeVersion] = createSignal(0);

  onMount(() => {
    const controller = new AbortController();
    void getDiskUsageVolumes(runtime.requestClient, controller.signal)
      .then(setVolumes)
      .catch(() => setVolumes([]));
    void refreshResult(controller.signal);
    onCleanup(() => controller.abort());
  });

  async function refreshResult(signal?: AbortSignal, nodeId?: number) {
    try {
      const [nextSummary, nextLayout] = await Promise.all([
        getDiskUsageSummary(runtime.requestClient, signal),
        getDiskUsageLayout(runtime.requestClient, nodeId, signal)
      ]);
      setSummary(nextSummary);
      setLayout(nextLayout);
      nodeCache.clear();
      setNodeVersion((version) => version + 1);
      setSelected(-1);
    } catch {
      // 还没扫过或请求被取消：保持空态，不弹错误。
    }
  }

  function nodeOf(nodeId: number) {
    void nodeVersion();
    if (nodeId < 0) {
      return undefined;
    }
    const known = nodeCache.get(nodeId);
    if (known) {
      return known;
    }
    void getDiskUsageNode(runtime.requestClient, nodeId)
      .then((node) => {
        nodeCache.set(nodeId, node);
        setNodeVersion((version) => version + 1);
      })
      .catch(() => undefined);
    return undefined;
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
            nodeOf={nodeOf}
            labelOf={(nodeId) => nodeOf(nodeId)?.name ?? ""}
            onSelect={setSelected}
            onActivate={(nodeId) => {
              const node = nodeOf(nodeId);
              if (node?.isDirectory) {
                drillInto(nodeId);
              }
            }}
            onContextMenu={(nodeId, x, y) => {
              const node = nodeOf(nodeId);
              if (node) {
                setMenu({ node, x, y });
              }
            }}
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
