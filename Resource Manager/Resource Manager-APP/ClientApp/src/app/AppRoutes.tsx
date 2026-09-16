import { Show } from "solid-js";
import type { Accessor } from "solid-js";
import { DetailsPage } from "../components/DetailsPage";
import { ControlWorkspace } from "../pages/ControlWorkspace";
import { DiskUsageWorkspace } from "../pages/DiskUsageWorkspace";
import type { SoftwareContextMenuTarget } from "../components/SoftwareContextMenu";
import { ManagementWorkspace } from "../pages/ManagementWorkspace";
import { MonitorPage } from "../pages/MonitorPage";
import { OptimizationWorkspace } from "../pages/OptimizationWorkspace";
import { SettingsWorkspace } from "../pages/SettingsWorkspace";
import { PageBoundary } from "../ui/patterns/PageBoundary.tsx";
import type { ManagementStore } from "../stores/managementStore";
import type { MigrationWorkbenchStore } from "../stores/migrationStore";
import type { MonitorStore } from "../stores/monitorStore";
import type { OptimizationStore } from "../stores/optimizationStore";
import type { SettingsStore } from "../stores/settingsStore";
import type { RuntimeCapabilitiesStore } from "../stores/runtimeCapabilitiesStore";
import type { AppAnimationMode, ManagedComponent, OptimizationReportItem, PageId, SoftwareRecord } from "../types";
import { uiText } from "../text.ts";

interface AppRoutesProps {
  animationMode: Exclude<AppAnimationMode, "auto">;
  activePage: Accessor<PageId>;
  monitor: MonitorStore;
  management: ManagementStore;
  migration: MigrationWorkbenchStore;
  optimization: OptimizationStore;
  settings: SettingsStore;
  runtimeCapabilities: RuntimeCapabilitiesStore;
  highlightedSoftwareId: Accessor<string | null>;
  onResourceTableSoftwareContextMenu: (event: MouseEvent, target: SoftwareContextMenuTarget) => void;
  /** 有右键菜单打开时，被右键的那一行钉住不动。 */
  contextMenuOpen: boolean;
  onManagementSoftwareContextMenu: (event: MouseEvent, target: SoftwareContextMenuTarget) => void;
  onOpenSoftwareDetail: (type: "component" | "software", value: ManagedComponent | SoftwareRecord) => void;
  onOpenSoftwareSettingsById: (softwareId: string, softwareName: string) => void;
  onInspectOptimizationTarget: (report: OptimizationReportItem) => void;
  /** 流程提示：某个动作做不成时当场说一句，不在页面上常驻。 */
  onNotice: (message: string) => void;
}

export function AppRoutes(props: AppRoutesProps) {
  return (
    <Show
      when={props.activePage() === "components"}
      fallback={
        <Show
          when={props.activePage() === "settings"}
          fallback={
            <Show
              when={props.activePage() === "control"}
              fallback={(
            <Show
              when={props.activePage() === "diskUsage"}
              fallback={(
            <Show
              when={props.activePage() === "details"}
              fallback={
                <Show
                  when={props.activePage() === "optimization"}
                  fallback={(
                    <PageBoundary name={uiText.page.monitor}>
                      <MonitorPage {...props} />
                    </PageBoundary>
                  )}
                >
                  <PageBoundary name={uiText.page.optimization}>
                    <OptimizationWorkspace {...props} />
                  </PageBoundary>
                </Show>
              }
            >
              <PageBoundary name={uiText.page.details}>
                <DetailsPage
                  runtimeCapabilities={props.runtimeCapabilities}
                  onOpenSoftwareSettings={props.onOpenSoftwareSettingsById}
                />
              </PageBoundary>
            </Show>
              )}
            >
              <PageBoundary name={uiText.page.diskUsage}>
                <DiskUsageWorkspace />
              </PageBoundary>
            </Show>
              )}
            >
              <PageBoundary name={uiText.page.control}>
                <ControlWorkspace onNotice={props.onNotice} />
              </PageBoundary>
            </Show>
          }
        >
          <PageBoundary name={uiText.page.settings}>
            <SettingsWorkspace
              settings={props.settings}
              runtimeCapabilities={props.runtimeCapabilities}
            />
          </PageBoundary>
        </Show>
      }
    >
      <PageBoundary name={uiText.page.components}>
        <ManagementWorkspace {...props} />
      </PageBoundary>
    </Show>
  );
}
