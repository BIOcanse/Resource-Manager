import { onCleanup, Show } from "solid-js";
import type { Accessor } from "solid-js";
import { Dashboard } from "../components/Dashboard";
import { ResourceBreakdown } from "../components/ResourceBreakdown";
import { ResourceTable } from "../components/ResourceTable";
import type { SoftwareContextMenuTarget } from "../components/SoftwareContextMenu";
import { MonitorWorkRegion } from "../monitor/MonitorWorkRegion";
import type { MonitorStore } from "../stores/monitorStore";
import type { AppAnimationMode } from "../types";
import { uiText } from "../text";
import {
  ObservationStateBoundary,
  ObservationStateNotice
} from "../components/ObservationStateNotice";
import { useInlineEditorFocus } from "../interactions/inlineEditorFocus";

interface MonitorPageProps {
  animationMode: Exclude<AppAnimationMode, "auto">;
  monitor: MonitorStore;
  highlightedSoftwareId: Accessor<string | null>;
  onResourceTableSoftwareContextMenu: (event: MouseEvent, target: SoftwareContextMenuTarget) => void;
}

export function MonitorPage(props: MonitorPageProps) {
  const monitor = props.monitor;
  let dashboardEditButton: HTMLButtonElement | undefined;
  let dashboardAddButton: HTMLButtonElement | undefined;
  let dashboardEditorRegion: HTMLDivElement | undefined;
  const dashboardAvailable = () => monitor.dashboardSettingsState() === "ready"
    || monitor.dashboardSettingsState() === "recovered";
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
  onCleanup(monitor.cancelAllMonitorEdits);

  return (
    <section id="monitorPage" class="page active-page">
      <div class="topbar monitor-toolbar">
        <div class="toolbar">
          <button
            ref={dashboardAddButton}
            class="secondary"
            classList={{ hidden: !monitor.editMode() }}
            type="button"
            aria-label="添加监控卡片"
            onClick={monitor.addCard}
          >
            添加卡片
          </button>
          <Show when={monitor.dashboardSaveState() === "error"}>
            <span class="save-state error">{uiText.common.saveFailed}</span>
          </Show>
          <Show when={monitor.editMode()}>
            <button
              class="secondary"
              type="button"
              aria-label="取消监控面板布局编辑"
              disabled={monitor.dashboardSaveState() === "saving"}
              onClick={monitor.cancelDashboardEdit}
            >
              取消
            </button>
          </Show>
          <button
            ref={dashboardEditButton}
            type="button"
            data-focus-key="monitor-dashboard-edit"
            aria-label={monitor.editMode() ? "保存监控面板布局" : "编辑监控面板布局"}
            title={!dashboardAvailable() ? "布局编辑暂不可用" : undefined}
            disabled={!dashboardAvailable() || monitor.dashboardSaveState() === "saving"}
            onClick={() => monitor.editMode() ? void monitor.saveDraftAndLeaveEditMode() : monitor.enterEditMode()}
          >
            {monitor.editMode()
              ? monitor.dashboardSaveState() === "saving" ? uiText.common.saving : "保存"
              : "编辑"}
          </button>
        </div>
      </div>
      <Show when={monitor.editMode() && monitor.dashboardSettingsState() === "recovered"}>
        <div class="monitor-settings-state warning" role="status">
          {monitor.dashboardSettingsSource()?.recoveryDisposition === "recoveredDefaultsAfterCorruption"
            ? "仪表盘配置已损坏并隔离，当前使用明确保存的安全默认配置。"
            : "仪表盘配置已从最近一次有效副本恢复。"}
        </div>
      </Show>
      <ObservationStateNotice
        state={monitor.catalogObservation()}
        label="指标目录"
        presentation="blocking-only"
        onRetry={() => void monitor.refreshCatalog()}
      />
          <MonitorWorkRegion
            label="实时指标"
          >
            <ObservationStateBoundary
              state={monitor.snapshotObservation()}
              label="实时指标"
              presentation="blocking-only"
              renderWhenUnavailable
            >
              <div
                ref={dashboardEditorRegion}
                role={monitor.editMode() ? "group" : undefined}
                aria-label={monitor.editMode() ? "监控面板布局编辑器" : undefined}
              >
                <Dashboard
                  cards={monitor.activeCards()}
                  catalog={monitor.catalog()}
                  snapshot={monitor.snapshot()}
                  editMode={monitor.editMode()}
                  metricSelectionAvailable={metricSelectionAvailable()}
                  onOpenMetricModal={monitor.openMetricModal}
                  onClearSlot={monitor.clearMetricSlot}
                  onMoveCard={monitor.moveDashboardCard}
                  onMoveMetricSlot={monitor.moveDashboardMetricSlot}
                />
              </div>
            </ObservationStateBoundary>
          </MonitorWorkRegion>
          <MonitorWorkRegion
            label="资源占用"
          >
            <ObservationStateBoundary
              state={monitor.resourceBarsObservation()}
              label="资源占用"
              presentation="blocking-only"
              renderWhenUnavailable
            >
              <ResourceBreakdown
                animationMode={props.animationMode}
                catalog={monitor.catalog()}
                editMode={monitor.resourceBarEditing()}
                editingAvailable={dashboardAvailable()}
                bars={monitor.activeResourceBars()}
                snapshotBars={monitor.orderedSnapshotBars()}
                selection={monitor.resourceSelection()}
                expanded={monitor.expandedResourceSegments()}
                saveState={monitor.resourceBarSaveState()}
                onToggleEdit={monitor.toggleResourceBarEditMode}
                onCancelEdit={monitor.cancelResourceBarEdit}
                onToggleBar={monitor.toggleResourceBar}
                onScaleModeChange={monitor.updateResourceBarScale}
                onSelect={monitor.selectResourceSegment}
                onReorderBar={monitor.reorderResourceBars}
              />
            </ObservationStateBoundary>
          </MonitorWorkRegion>
          <MonitorWorkRegion
            label="资源列表"
          >
            <ObservationStateBoundary
              state={activeResourceTableObservation()}
              label="资源列表"
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
                sortColumnId={monitor.resourceTableSort().columnId}
                sortDirection={monitor.resourceTableSort().direction}
                expandedSoftwareIds={monitor.expandedResourceTableRows()}
                highlightedSoftwareId={props.highlightedSoftwareId()}
                saveState={monitor.resourceTableSaveState()}
                onModeChange={monitor.changeResourceTableMode}
                onToggleEdit={monitor.toggleResourceTableEditMode}
                onCancelEdit={monitor.cancelResourceTableEdit}
                onSortChange={monitor.updateResourceTableSort}
                onToggleColumn={monitor.toggleResourceTableColumn}
                onColumnWidthChange={monitor.updateResourceTableColumnWidth}
                onToggleExpand={monitor.toggleResourceTableExpand}
                onSoftwareContextMenu={props.onResourceTableSoftwareContextMenu}
                onReorderColumn={monitor.reorderResourceTableColumns}
              />
            </ObservationStateBoundary>
          </MonitorWorkRegion>
    </section>
  );
}
