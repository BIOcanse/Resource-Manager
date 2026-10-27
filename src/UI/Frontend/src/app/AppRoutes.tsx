import { Show, lazy, onCleanup, onMount } from "solid-js";
import type { Accessor } from "solid-js";
import type { SoftwareContextMenuTarget } from "../components/SoftwareContextMenu";
import { MonitorPage } from "../features/monitor/MonitorPage";
import { PageBoundary } from "../ui/patterns/PageBoundary.tsx";
import type { ManagementStore } from "../stores/managementStore";
import type { MonitorStore } from "../stores/monitorStore";
import type { OptimizationStore } from "../stores/optimizationStore";
import type { SettingsStore } from "../stores/settingsStore";
import type { ProductUpdateStore } from "../stores/productUpdateStore";
import type { RuntimeCapabilitiesStore } from "../stores/runtimeCapabilitiesStore";
import type { ManagedComponent, OptimizationReportItem, PageId, SoftwareRecord } from "../types";
import type { AnimationStyleId } from "../presentation/animationStyle.ts";
import { uiText } from "../text.ts";
import type { ConfirmDialogRequest } from "../components/AppFeedback";
import type { DetailsTab } from "./navigation/detailsNavigation.ts";

// 首屏是监视控制台；其余页面各自一个分包，首屏脚本只含监视页。
// 分包在首屏渲染后由 preloadRoutePages 预先载入，换页时已就绪，不出现空白帧。
const ManagementWorkspace = lazy(() => import("../features/management/ManagementWorkspace")
  .then((module) => ({ default: module.ManagementWorkspace })));
const OptimizationWorkspace = lazy(() => import("../features/optimization/OptimizationWorkspace")
  .then((module) => ({ default: module.OptimizationWorkspace })));
const DiskUsageWorkspace = lazy(() => import("../features/diskUsage/DiskUsageWorkspace")
  .then((module) => ({ default: module.DiskUsageWorkspace })));
const ControlWorkspace = lazy(() => import("../features/control/ControlWorkspace")
  .then((module) => ({ default: module.ControlWorkspace })));
const DetailsPage = lazy(() => import("../features/details/DetailsPage")
  .then((module) => ({ default: module.DetailsPage })));
const SettingsWorkspace = lazy(() => import("../features/settings/SettingsWorkspace")
  .then((module) => ({ default: module.SettingsWorkspace })));

/** 载入全部非首屏页面的分包。载入失败不在这里处理：进入该页时由页面边界显示错误。 */
function preloadRoutePages(): Promise<unknown> {
  return Promise.allSettled([
    ManagementWorkspace.preload(),
    OptimizationWorkspace.preload(),
    DiskUsageWorkspace.preload(),
    ControlWorkspace.preload(),
    DetailsPage.preload(),
    SettingsWorkspace.preload()
  ]);
}

interface AppRoutesProps {
  animationStyle: AnimationStyleId;
  activePage: Accessor<PageId>;
  /** 详细信息选中的二级分页（分页树）。 */
  detailsTab: Accessor<DetailsTab>;
  onTrustedAccessHeadingChange?: (heading: HTMLElement | undefined) => void;
  monitor: MonitorStore;
  management: ManagementStore;
  optimization: OptimizationStore;
  settings: SettingsStore;
  productUpdates: ProductUpdateStore;
  runtimeCapabilities: RuntimeCapabilitiesStore;
  highlightedSoftwareId: Accessor<string | null>;
  onResourceTableSoftwareContextMenu: (event: MouseEvent, target: SoftwareContextMenuTarget) => void;
  /** 有右键菜单打开时，被右键的那一行钉住不动。 */
  contextMenuOpen: boolean;
  onManagementSoftwareContextMenu: (event: MouseEvent, target: SoftwareContextMenuTarget) => void;
  onOpenSoftwareDetail: (type: "component" | "software", value: ManagedComponent | SoftwareRecord) => void;
  onOpenSoftwareSettingsById: (softwareId: string, softwareName: string) => void;
  /** 设置 → 性能浮层：软件详细信息对话框是否开着，以及打开某个软件停在叠加层分页。 */
  softwareDetailOpen: boolean;
  onOpenSoftwareOverlay: (software: SoftwareRecord) => void;
  onInspectOptimizationTarget: (report: OptimizationReportItem) => void;
  /** 流程提示：某个动作做不成时当场说一句，不在页面上常驻。 */
  onNotice: (message: string) => void;
  /** 页面动作失败时的错误提示。 */
  onActionError: (error: unknown, fallback: string) => void;
  /** 应用统一的确认对话框（useGlobalFeedback）；页面里不可撤销的操作用它确认。 */
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
}

export function AppRoutes(props: AppRoutesProps) {
  onMount(() => {
    // 首屏渲染完、主线程空闲时再载入其余页面；最迟 2 秒后开始。
    const idle = requestIdleCallback(() => void preloadRoutePages(), { timeout: 2_000 });
    onCleanup(() => cancelIdleCallback(idle));
  });

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
                  activeTab={props.detailsTab()}
                  software={props.management.software()}
                  runtimeCapabilities={props.runtimeCapabilities}
                  onOpenSoftwareSettings={props.onOpenSoftwareSettingsById}
                  confirmDialog={props.confirmDialog}
                />
              </PageBoundary>
            </Show>
              )}
            >
              <PageBoundary name={uiText.page.diskUsage}>
                <DiskUsageWorkspace onActionError={props.onActionError} />
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
              onTrustedAccessHeadingChange={props.onTrustedAccessHeadingChange}
              settings={props.settings}
              productUpdates={props.productUpdates}
              runtimeCapabilities={props.runtimeCapabilities}
              softwareDetailOpen={props.softwareDetailOpen}
              onOpenSoftwareOverlay={props.onOpenSoftwareOverlay}
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
