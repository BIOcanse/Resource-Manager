import { createEffect, createSignal, onCleanup, onMount, on, Show } from "solid-js";
import { openPath, openProperties } from "../../api";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext.tsx";
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
  const frontendWork = useFrontendWork();
  const owner = runtime.diskUsage;
  const {
    volumes, volumesReadState, scanning, scanFailed, summary, layout, resultReadFailure,
    selectedRootIndex, selected, setSelected, drillStack, queryBusy, queryPage, resetNonce,
    readVolumes, startScan, factsOf, labelOf, onViewChanged, resetView, drillInto,
    navigateUp, selectRoot, changeQueryPage
  } = owner;
  const [menu, setMenu] = createSignal<{ node: DiskUsageNode; x: number; y: number } | null>(null);
  createEffect(on(frontendWork.frontendVisible, visible => owner.setActive(visible)));
  onMount(() => {
    const refreshVolumes = () => void owner.readVolumes();
    window.addEventListener("focus", refreshVolumes);
    onCleanup(() => window.removeEventListener("focus", refreshVolumes));
  });
  onCleanup(() => owner.setActive(false));

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
