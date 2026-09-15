import { onCleanup, Show } from "solid-js";
import type { Accessor } from "solid-js";
import { Dashboard } from "../components/Dashboard";
import { ResourceBreakdown } from "../components/ResourceBreakdown";
import { ResourceTable } from "../components/ResourceTable";
import type { SoftwareContextMenuTarget } from "../components/SoftwareContextMenu";
import { MonitorWorkRegion } from "../monitor/MonitorWorkRegion";
import type { MonitorStore } from "../stores/monitorStore";
import type { AppAnimationMode } from "../types";
import { uiText } from "../text.ts";
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
  /** 有右键菜单打开时，被右键的那一行钉住不动。 */
  contextMenuOpen: boolean;
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
            aria-label={uiText.monitorPage.addCardLabel}
            onClick={monitor.addCard}
          >
            {uiText.monitorPage.addCard}
          </button>
          <Show when={monitor.dashboardSaveState() === "error"}>
            <span class="save-state error">{uiText.common.saveFailed}</span>
          </Show>
          <Show when={monitor.editMode()}>
            <button
              class="secondary"
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
            type="button"
            data-focus-key="monitor-dashboard-edit"
            aria-label={monitor.editMode() ? uiText.monitorPage.saveLayoutLabel : uiText.monitorPage.editLayoutLabel}
            title={!dashboardAvailable() ? uiText.monitorPage.layoutEditUnavailable : undefined}
            disabled={!dashboardAvailable() || monitor.dashboardSaveState() === "saving"}
            onClick={() => monitor.editMode() ? void monitor.saveDraftAndLeaveEditMode() : monitor.enterEditMode()}
          >
            {monitor.editMode()
              ? monitor.dashboardSaveState() === "saving" ? uiText.common.saving : uiText.monitorPage.save
              : uiText.monitorPage.edit}
          </button>
        </div>
      </div>
      <Show when={monitor.editMode() && monitor.dashboardSettingsState() === "recovered"}>
        <div class="monitor-settings-state warning" role="status">
          {monitor.dashboardSettingsSource()?.recoveryDisposition === "recoveredDefaultsAfterCorruption"
            ? uiText.monitorPage.dashboardQuarantined
            : uiText.monitorPage.dashboardRecovered}
        </div>
      </Show>
      <ObservationStateNotice
        state={monitor.catalogObservation()}
        label={uiText.monitorPage.metricCatalog}
        presentation="blocking-only"
        onRetry={() => void monitor.refreshCatalog()}
      />
          <MonitorWorkRegion
            label={uiText.monitorPage.liveMetrics}
          >
            <ObservationStateBoundary
              state={monitor.snapshotObservation()}
              label={uiText.monitorPage.liveMetrics}
              presentation="blocking-only"
              renderWhenUnavailable
            >
              <div
                ref={dashboardEditorRegion}
                role={monitor.editMode() ? "group" : undefined}
                aria-label={monitor.editMode() ? uiText.monitorPage.layoutEditor : undefined}
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
            label={uiText.monitorPage.resourceUsage}
          >
            <ObservationStateBoundary
              state={monitor.resourceBarsObservation()}
              label={uiText.monitorPage.resourceUsage}
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
            label={uiText.monitorPage.resourceList}
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
                contextMenuOpen={props.contextMenuOpen}
                onReorderColumn={monitor.reorderResourceTableColumns}
              />
            </ObservationStateBoundary>
          </MonitorWorkRegion>
    </section>
  );
}
