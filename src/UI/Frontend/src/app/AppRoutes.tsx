import { lazy, onCleanup, onMount } from "solid-js";
import type { Accessor } from "solid-js";
import type { SoftwareContextMenuTarget } from "../components/SoftwareContextMenu";
import { MonitorPage } from "../features/monitor/MonitorPage";
import { PageBoundary } from "../ui/patterns/PageBoundary.tsx";
import { PageHost } from "./navigation/pageHost.tsx";
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
import type { ManageSubpage } from "../features/manage/manageNavigation.ts";
import { SettingsPanelProvider } from "../features/settings/SettingsPanelContext.tsx";
import { createSettingsPanelBindings } from "../features/settings/settingsPanelBindings.ts";

// Only the monitoring page belongs to the initial bundle.
// Preload other page bundles after first render to avoid blank frames on navigation.
const ManagementWorkspace = lazy(() => import("../features/management/ManagementWorkspace")
  .then((module) => ({ default: module.ManagementWorkspace })));
const ManagePage = lazy(() => import("../features/manage/ManagePage")
  .then((module) => ({ default: module.ManagePage })));
const ControlWorkspace = lazy(() => import("../features/control/ControlWorkspace")
  .then((module) => ({ default: module.ControlWorkspace })));
const DetailsPage = lazy(() => import("../features/details/DetailsPage")
  .then((module) => ({ default: module.DetailsPage })));
const SettingsWorkspace = lazy(() => import("../features/settings/SettingsWorkspace")
  .then((module) => ({ default: module.SettingsWorkspace })));

/** Preload other pages; their page boundaries handle load failures when opened. */
function preloadRoutePages(): Promise<unknown> {
  return Promise.allSettled([
    ManagementWorkspace.preload(),
    ManagePage.preload(),
    ControlWorkspace.preload(),
    DetailsPage.preload(),
    SettingsWorkspace.preload()
  ]);
}

interface AppRoutesProps {
  animationStyle: AnimationStyleId;
  activePage: Accessor<PageId>;
  /** Selected device-information subpage. */
  detailsTab: Accessor<DetailsTab>;
  /** Selected management subpage. */
  manageSubpage: Accessor<ManageSubpage>;
  onTrustedAccessFocusTargetChange?: (target: HTMLElement | undefined) => void;
  monitor: MonitorStore;
  management: ManagementStore;
  optimization: OptimizationStore;
  settings: SettingsStore;
  productUpdates: ProductUpdateStore;
  runtimeCapabilities: RuntimeCapabilitiesStore;
  highlightedSoftwareId: Accessor<string | null>;
  onResourceTableSoftwareContextMenu: (event: MouseEvent, target: SoftwareContextMenuTarget) => void;
  /** Pin the target row while its context menu is open. */
  contextMenuOpen: boolean;
  onManagementSoftwareContextMenu: (event: MouseEvent, target: SoftwareContextMenuTarget) => void;
  onOpenSoftwareDetail: (type: "component" | "software", value: ManagedComponent | SoftwareRecord) => void;
  onOpenSoftwareSettingsById: (softwareId: string, softwareName: string) => void;
  /** Software dialog visibility and navigation to its overlay tab. */
  softwareDetailOpen: boolean;
  onOpenSoftwareOverlay: (software: SoftwareRecord) => void;
  onInspectOptimizationTarget: (report: OptimizationReportItem) => void;
  /** Transient feedback when an action cannot proceed. */
  onNotice: (message: string) => void;
  /** Report a failed page action. */
  onActionError: (error: unknown, fallback: string) => void;
  /** Report a completed page action. */
  onActionSucceeded: (title: string, message: string) => void;
  /** Shared confirmation dialog for irreversible page actions. */
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
}

export function AppRoutes(props: AppRoutesProps) {
  onMount(() => {
    // Preload after first render when idle, with a two-second deadline.
    const idle = requestIdleCallback(() => void preloadRoutePages(), { timeout: 2_000 });
    onCleanup(() => cancelIdleCallback(idle));
  });

  // Settings, management, and firmware panels share one settings draft.
  const settingsPanelBindings = createSettingsPanelBindings({
    settings: props.settings,
    productUpdates: props.productUpdates,
    runtimeCapabilities: props.runtimeCapabilities,
    softwareDetailOpen: () => props.softwareDetailOpen,
    onOpenSoftwareOverlay: (software) => props.onOpenSoftwareOverlay(software),
    confirmDialog: (request) => props.confirmDialog(request),
    get onTrustedAccessFocusTargetChange() { return props.onTrustedAccessFocusTargetChange; }
  });
  // Each page has its own host; retained pages hide on leave, and others unmount.
  const current = (page: PageId) => props.activePage() === page;
  return (
    <SettingsPanelProvider bindings={settingsPanelBindings}>
      <PageHost page="monitor" active={current("monitor")}>
        <PageBoundary name={uiText.page.monitor}>
          <MonitorPage {...props} />
        </PageBoundary>
      </PageHost>
      <PageHost page="components" active={current("components")}>
        <PageBoundary name={uiText.page.components}>
          <ManagementWorkspace {...props} />
        </PageBoundary>
      </PageHost>
      <PageHost page="manage" active={current("manage")}>
        <PageBoundary name={uiText.page.manage}>
          <ManagePage
            activeSubpage={props.manageSubpage()}
            optimization={props.optimization}
            settings={props.settings}
            optimizationEnabled={props.runtimeCapabilities.optimizationEnabled()}
            software={props.management.software()}
            keepSmartRecordHistory={props.settings.settings().performance?.keepSmartRecordHistory === true}
            onInspectOptimizationTarget={props.onInspectOptimizationTarget}
            confirmDialog={props.confirmDialog}
            onActionSucceeded={props.onActionSucceeded}
            onActionError={props.onActionError}
          />
        </PageBoundary>
      </PageHost>
      <PageHost page="control" active={current("control")}>
        <PageBoundary name={uiText.page.control}>
          <ControlWorkspace onNotice={props.onNotice} discardSettingsDraft={props.settings.discardDraftChanges} />
        </PageBoundary>
      </PageHost>
      <PageHost page="details" active={current("details")}>
        <PageBoundary name={uiText.page.details}>
          <DetailsPage
            activeTab={props.detailsTab()}
            runtimeCapabilities={props.runtimeCapabilities}
            onOpenSoftwareSettings={props.onOpenSoftwareSettingsById}
          />
        </PageBoundary>
      </PageHost>
      <PageHost page="settings" active={current("settings")}>
        <PageBoundary name={uiText.page.settings}>
          <SettingsWorkspace settings={props.settings} />
        </PageBoundary>
      </PageHost>
    </SettingsPanelProvider>
  );
}
