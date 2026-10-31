import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { Show } from "solid-js";
import type { Accessor } from "solid-js";
import { Dashboard } from "./Dashboard";
import { ResourceBreakdown } from "../resourceBreakdown/ResourceBreakdown";
import { ResourceTable } from "../resourceTable/ResourceTable";
import type { SoftwareContextMenuTarget } from "../../components/SoftwareContextMenu";
import { MonitorWorkRegion } from "./MonitorWorkRegion";
import type { MonitorStore } from "../../stores/monitorStore";
import type { AnimationStyleId } from "../../presentation/animationStyle.ts";
import { uiText } from "../../text.ts";
import {
  ObservationStateBoundary,
  ObservationStateNotice
} from "../../components/ObservationStateNotice";
import { useInlineEditorFocus } from "../../interactions/inlineEditorFocus";
import { onPageLeave } from "../../app/navigation/pageHost.tsx";

interface MonitorPageProps {
  animationStyle: AnimationStyleId;
  monitor: MonitorStore;
  highlightedSoftwareId: Accessor<string | null>;
  onResourceTableSoftwareContextMenu: (event: MouseEvent, target: SoftwareContextMenuTarget) => void;
  /** 有右键菜单打开时，被右键的那一行钉住不动。 */
  contextMenuOpen: boolean;
}

export function MonitorPage(props: MonitorPageProps) {
  const monitor = props.monitor;
  let dashboardEditButton: HTMLButtonElement | undefined;
  let dashboardAddButton: HTMLButtonElement | undefined;
  let dashboardEditorRegion: HTMLDivElement | undefined;
  const dashboardAvailable = () => monitor.dashboardSettingsState() === "ready";
  const activeResourceTableObservation = () => monitor.resourceTableMode() === "performance"
    ? monitor.snapshotObservation()
    : monitor.resourceTableObservation();
  const metricSelectionAvailable = () => monitor.catalogObservation().status === "ready"
    || monitor.catalogObservation().status === "stale";
  useInlineEditorFocus({
    active: monitor.editMode,
    opener: () => dashboardEditButton,
    editor: () => dashboardEditorRegion,
    initialFocus: () => dashboardAddButton
  });
  // 离开监视页（常驻时被隐藏，或卸下）时放弃未保存的编辑。
  onPageLeave(monitor.cancelAllMonitorEdits);

  return (
    <section id="monitorPage" class="page active-page">
      {/* 卡片区和下面两个面板一样：标题在左，编辑按钮在右。 */}
      <div class="panel-header monitor-toolbar">
        <h2>{uiText.monitorPage.liveMetrics}</h2>
        <div class="panel-header-actions">
          <button
            ref={dashboardAddButton}
            class="secondary panel-refresh-button"
            classList={{ hidden: !monitor.editMode() }}
            type="button"
            aria-label={uiText.monitorPage.addCardLabel}
            onClick={monitor.addCard}
          >
            {uiText.monitorPage.addCard}
          </button>
          <Show when={monitor.dashboardSaveState() === "error"}>
            <span class="save-state error">{uiText.common.saveFailed}</span>
          </Show>
          {/* 按钮用不了的原因写在按钮旁边（禁用的按钮收不到指针）。 */}
          <Show when={!dashboardAvailable()}>
            <span class="save-state">{uiText.monitorPage.layoutEditUnavailable}</span>
          </Show>
          <Show when={dashboardAvailable() && monitor.editMode() && !metricSelectionAvailable()}>
            <span class="save-state">{uiText.dashboard.catalogUnavailable}</span>
          </Show>
          <Show when={monitor.editMode()}>
            <button
              class="secondary panel-refresh-button"
              type="button"
              aria-label={uiText.monitorPage.cancelEditLabel}
              disabled={monitor.dashboardSaveState() === "saving"}
              onClick={monitor.cancelDashboardEdit}
            >
              {uiText.monitorPage.cancel}
            </button>
          </Show>
          <button
            ref={dashboardEditButton}
            class="secondary panel-refresh-button"
            type="button"
            data-focus-key="monitor-dashboard-edit"
            aria-label={monitor.editMode() ? uiText.monitorPage.saveLayoutLabel : uiText.monitorPage.editLayoutLabel}
            disabled={!dashboardAvailable() || monitor.dashboardSaveState() === "saving"}
            onClick={() => monitor.editMode() ? void monitor.saveDraftAndLeaveEditMode() : monitor.enterEditMode()}
          >
            <StableLabel
              labels={{ edit: uiText.monitorPage.edit, save: uiText.monitorPage.save, saving: uiText.common.saving }}
              current={monitor.editMode() ? monitor.dashboardSaveState() === "saving" ? "saving" : "save" : "edit"}
            />
          </button>
        </div>
      </div>
      <ObservationStateNotice
        state={monitor.catalogObservation()}
        label={uiText.monitorPage.metricCatalog}
        presentation="blocking-only"
        onRetry={() => void monitor.refreshCatalog()}
      />
      {/* 卡片、资源条和表列都来自看板布局；布局读回之前不知道有几张卡片、几条资源条、哪些列，下面三个区都不摆，
          只把正在加载读给读屏。布局到时三个区一起出现，已经显示的东西（上面的工具栏）不被推动
          （用户 10-27：「让占位和加载后状态的逻辑占位大小相等」）。读不到布局时照常显示三个区和各自的状态。 */}
      <Show
        when={monitor.dashboardSettingsState() !== "loading"}
        fallback={<p class="sr-only" role="status">{uiText.observationNotice.loading(uiText.monitorPage.liveMetrics)}</p>}
      >
        <MonitorWorkRegion
          label={uiText.monitorPage.liveMetrics}
          workId="monitor.metrics"
        >
          <ObservationStateBoundary
            state={monitor.snapshotObservation()}
            label={uiText.monitorPage.liveMetrics}
            presentation="blocking-only"
            renderWhenUnavailable
          >
            {/* 设备字段那一路单独提示：只影响用设备字段的卡片，那些卡片写“读取失败”，其他卡片照常。推送源自己重连，没有手动重试。 */}
            <ObservationStateNotice
              state={monitor.fieldObservation()}
              label={uiText.monitorPage.deviceFieldReadings}
              presentation="blocking-only"
            />
            <div
              ref={dashboardEditorRegion}
              role={monitor.editMode() ? "group" : undefined}
              aria-label={monitor.editMode() ? uiText.monitorPage.layoutEditor : undefined}
            >
              <Dashboard
                cards={monitor.activeCards()}
                catalog={monitor.cardCatalog()}
                gpuNames={monitor.gpuNames()}
                snapshot={monitor.cardSnapshot()}
                pendingMetricIds={monitor.cardPendingMetricIds()}
                failedMetricIds={monitor.cardFailedMetricIds()}
                trends={monitor.cardTrends()}
                editMode={monitor.editMode()}
                metricSelectionAvailable={metricSelectionAvailable()}
                onOpenMetricModal={monitor.openMetricModal}
                onClearSlot={monitor.clearMetricSlot}
                onResizeSlot={monitor.resizeMetricSlot}
                onMoveCard={monitor.moveDashboardCard}
                onMoveMetricSlot={monitor.moveDashboardMetricSlot}
              />
            </div>
          </ObservationStateBoundary>
        </MonitorWorkRegion>
        <MonitorWorkRegion
          label={uiText.monitorPage.resourceUsage}
          workId="monitor.bars"
        >
          <ObservationStateBoundary
            state={monitor.resourceBarsObservation()}
            label={uiText.monitorPage.resourceUsage}
            presentation="blocking-only"
            renderWhenUnavailable
          >
            <ResourceBreakdown
              animationStyle={props.animationStyle}
              catalog={monitor.catalog()}
              gpuNames={monitor.gpuNames()}
              editMode={monitor.resourceBarEditing()}
              editingAvailable={dashboardAvailable()}
              bars={monitor.activeResourceBars()}
              snapshotBars={monitor.orderedSnapshotBars()}
              processFields={monitor.processFields()}
              observation={monitor.resourceBarsObservation()}
              processDetails={monitor.resourceProcessDetails()}
              processDetailsObservation={monitor.resourceProcessDetailsObservation()}
              selection={monitor.resourceSelection()}
              saveState={monitor.resourceBarSaveState()}
              onToggleEdit={monitor.toggleResourceBarEditMode}
              onCancelEdit={monitor.cancelResourceBarEdit}
              onToggleBar={monitor.toggleResourceBar}
              onScaleModeChange={monitor.updateResourceBarScale}
              onSourceChange={monitor.changeResourceBarSource}
              onSelect={monitor.selectResourceSegment}
              onToggle={monitor.toggleResourceSegment}
              onReorderBar={monitor.reorderResourceBars}
            />
          </ObservationStateBoundary>
        </MonitorWorkRegion>
        <MonitorWorkRegion
          label={uiText.monitorPage.resourceList}
          workId="monitor.table"
        >
          <ObservationStateBoundary
            state={activeResourceTableObservation()}
            label={uiText.monitorPage.resourceList}
            presentation="blocking-only"
            renderWhenUnavailable
          >
            <ResourceTable
              editMode={monitor.resourceTableEditing()}
              editingAvailable={dashboardAvailable()}
              mode={monitor.resourceTableMode()}
              catalog={monitor.catalog()}
              metricSnapshot={monitor.snapshot()}
              metricObservation={monitor.snapshotObservation()}
              columnSettings={monitor.activeResourceTableColumns()}
              snapshot={monitor.resourceTableSnapshot()}
              observation={activeResourceTableObservation()}
              gpuNames={monitor.gpuNames()}
              sortColumnId={monitor.resourceTableSort().columnId}
              sortDirection={monitor.resourceTableSort().direction}
              expandedSoftwareIds={monitor.expandedResourceTableRows()}
              highlightedSoftwareId={props.highlightedSoftwareId()}
              saveState={monitor.resourceTableSaveState()}
              onModeChange={monitor.changeResourceTableMode}
              onToggleEdit={monitor.toggleResourceTableEditMode}
              onCancelEdit={monitor.cancelResourceTableEdit}
              onSortColumn={monitor.cycleResourceTableSort}
              onToggleColumn={monitor.toggleResourceTableColumn}
              onAddColumn={monitor.addResourceTableColumn}
              onRemoveColumn={monitor.removeResourceTableColumn}
              processFields={monitor.processFields()}
              onColumnSourceChange={monitor.changeResourceTableColumnSource}
              onColumnWidthChange={monitor.updateResourceTableColumnWidth}
              onToggleExpand={monitor.toggleResourceTableExpand}
              onSoftwareContextMenu={props.onResourceTableSoftwareContextMenu}
              contextMenuOpen={props.contextMenuOpen}
              onReorderColumn={monitor.reorderResourceTableColumns}
            />
          </ObservationStateBoundary>
        </MonitorWorkRegion>
      </Show>
    </section>
  );
}
