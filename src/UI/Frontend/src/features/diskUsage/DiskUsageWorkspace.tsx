import { createSignal, Show } from "solid-js";
import { openPath, openProperties } from "../../api";
import { onPageLeave } from "../../app/navigation/pageHost.tsx";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds.ts";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface.ts";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand.tsx";
import { useWhileNeeded } from "../../frontendWork/useWhileNeeded.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { DiskUsageContextMenu } from "./DiskUsageContextMenu";
import { DiskUsagePage } from "./DiskUsagePage";
import { DiskUsageResultPanel } from "./DiskUsageResultPanel";
import { getDiskUsageNode } from "./diskUsageApi.ts";
import type { DiskUsageNode } from "./diskUsageLayoutTypes.ts";
import { uiText } from "../../text.ts";

/** Page-local interactions consume the runtime's accepted disk result directly. */
export function DiskUsageWorkspace(props: {
  onActionError: (error: unknown, fallback: string) => void;
}) {
  const runtime = useFrontendRuntime();
  const owner = runtime.diskUsage;
  const {
    volumes, volumesReadState, scanning, scanFailed, summary, layout, resultReadFailure,
    selectedRootIndex, selected, setSelected, drillStack, queryBusy, queryPage, resetNonce,
    readVolumes, startScan, factsOf, labelOf, onViewChanged, resetView, drillInto,
    navigateUp, selectRoot, changeQueryPage
  } = owner;
  const [menu, setMenu] = createSignal<{ node: DiskUsageNode; x: number; y: number } | null>(null);
  // 右键菜单要先取单节点事实才打开；每次右键和离开页面都换一号，晚到的回复对不上号就不再打开。
  let menuRequest = 0;
  // 页面工作随整页可见区域：页面隐藏（离开常驻页、窗口隐藏）时停，回来时恢复。
  const pageWork = frontendWorkIds.diskUsagePage;
  useFrontendVisibilityDemand(pageWork, [pageWork]);
  // 进入：owner 活动（读卷列表和结果），窗口回到前台时重读卷列表；退出时都撤销。
  const refreshVolumes = () => void owner.readVolumes();
  useWhileNeeded(pageWork, () => {
    owner.setActive(true);
    window.addEventListener("focus", refreshVolumes);
    return () => {
      window.removeEventListener("focus", refreshVolumes);
      owner.setActive(false);
    };
  });
  onPageLeave(() => {
    menuRequest++;
    setMenu(null);
  });

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
        onActionError={props.onActionError}
        visibilitySurface={frontendVisibilitySurface(pageWork, [pageWork])}
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
            onSelectRoot={(rootIndex) => { setMenu(null); selectRoot(rootIndex); }}
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
              const request = ++menuRequest;
              void getDiskUsageNode(runtime.requestClient, nodeId)
                .then((node) => { if (request === menuRequest) setMenu({ node, x, y }); })
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
              void openPath(path, true).catch((error: unknown) =>
                props.onActionError(error, uiText.softwareActions.openFileLocationFailed));
            }}
            onProperties={() => {
              const path = current().node.fullPath;
              setMenu(null);
              void openProperties(path).catch((error: unknown) =>
                props.onActionError(error, uiText.softwareActions.openPropertiesFailed));
            }}
            onCopyPath={() => {
              const path = current().node.fullPath;
              setMenu(null);
              void navigator.clipboard?.writeText(path).catch((error: unknown) =>
                props.onActionError(error, uiText.misc.copyFailed));
            }}
          />
        )}
      </Show>
    </>
  );
}
