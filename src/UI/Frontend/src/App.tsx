import { FrontendReadIntervalsProvider } from "./app/FrontendReadIntervalsContext.tsx";
import { batch, createEffect, createMemo, createSignal, lazy, onCleanup, onMount } from "solid-js";
import { useGlobalFeedback } from "./app/useGlobalFeedback";
import { AppRoutes } from "./app/AppRoutes";
import { AppShell } from "./app/AppShell";
import { useRuntimeReadContext } from "./app/useRuntimeReadContext";
import { useShellResizeBinding } from "./app/useShellResizeBinding";
import { useManagementInventory } from "./features/management/useManagementInventory";
import { useSoftwareActions } from "./app/useSoftwareActions";
import { ConfirmDialogHost, ToastHost } from "./components/AppFeedback";
import { ManualSoftwareModal } from "./features/management/components/ManualSoftwareModal";
import { MetricDependencyState, MetricModal } from "./features/monitor/MetricModal";
import { StandardContextMenu } from "./components/StandardContextMenu";
import { TooltipLayer } from "./ui/primitives/TooltipLayer.tsx";
import { FrontendWorkProvider } from "./frontendWork/FrontendWorkContext";
import { useFrontendWorkController } from "./frontendWork/useFrontendWorkController";
import { frontendVisibilitySourceKinds, frontendWorkIds } from "./frontendWork/frontendWorkIds";
import { useFrontendRuntime } from "./frontendRuntime/FrontendRuntimeContext";
import type { ManagementSubpageId } from "./features/management/managementNavigation";
import { createNavigationTree } from "./app/navigation/navigationTree.ts";
import { createManagementNavigation } from "./app/navigation/managementNavigation.ts";
import { createDetailsNavigation } from "./app/navigation/detailsNavigation.ts";
import { createManageNavigation, type ManageSubpage } from "./features/manage/manageNavigation.ts";
import { publishAnimationStyle, publishCommittedDocumentAppearance } from "./presentation/documentAppearance";
import { normalizeUiAppearance } from "./presentation/uiStyles";
import { animationStyle, animationStyleMoves } from "./presentation/animationStyle.ts";
import { resolveResourceBarFrameRate } from "./presentation/resourceBarFrameRate.ts";
import { createManagementStore } from "./stores/managementStore";
import { createMonitorStore, type MonitorStore } from "./stores/monitorStore";
import { createOptimizationStore } from "./stores/optimizationStore";
import {
  createSettingsStore,
  normalizeBarColorMode,
  resolveEffectiveFontSmoothing
} from "./stores/settingsStore";
import { createRuntimeCapabilitiesStore } from "./stores/runtimeCapabilitiesStore";
import { AccessRequestSnapshotProvider } from "./features/trustedAccess/AccessRequestSnapshotContext.tsx";
import { TrustedAccessPendingBanner } from "./features/settings/components/TrustedAccessPendingBanner.tsx";
import { createProductUpdateStore } from "./stores/productUpdateStore";
import { productUpdateCopy } from "./features/settings/productUpdatePresentation.ts";
import { resolveLanguageMode } from "./i18n/settingsLanguages";
import { applyLanguage, currentSettingsText, uiText } from "./text.ts";
import { LanguageLoadNotice } from "./i18n/LanguageLoadNotice";
import { applyByteUnitMode, formatBytes } from "./presentation/byteUnits.ts";
import { bindBackendNotificationToasts } from "./features/notifications/backendNotificationToasts.ts";
import { createCrashReportStore } from "./features/crashReports/crashReportStore.ts";
import { CrashReportProvider } from "./features/crashReports/CrashReportContext.tsx";
import { CrashReportDialog } from "./features/crashReports/CrashReportDialog.tsx";
import { CrashDumpRequestCard } from "./features/crashReports/CrashDumpRequestCard.tsx";
import { cpuMonopolyActionReasonText } from "./features/cpuMonopoly/cpuMonopolyText.ts";
import type {
  MetricDefinition,
  PageId
} from "./types";

// Preload the separate software-details and task-center bundles during initial rendering.
const SoftwareDetailModal = lazy(() => import("./components/SoftwareDetailModal")
  .then((module) => ({ default: module.SoftwareDetailModal })));
const TaskCenterDialog = lazy(() => import("./ui/task-center/TaskCenterDialog")
  .then((module) => ({ default: module.TaskCenterDialog })));

export default function App() {
  const frontendRuntime = useFrontendRuntime();
  const [activePage, setActivePage] = createSignal<PageId>("monitor");
  const [accessRequestsFocusRequested, setAccessRequestsFocusRequested] = createSignal(false);
  const [trustedAccessFocusTarget, setTrustedAccessFocusTarget] = createSignal<HTMLElement>();
  const [taskCenterOpen, setTaskCenterOpen] = createSignal(false);
  const [taskCenterSnapshot, setTaskCenterSnapshot] = createSignal(
    frontendRuntime.taskCenter.snapshot);
  const unsubscribeTaskCenter = frontendRuntime.taskCenter.subscribe(
    setTaskCenterSnapshot);
  const frontendWork = useFrontendWorkController();
  createEffect(() => frontendRuntime.operationRegistry.setActive(frontendWork.frontendVisible()));
  onCleanup(() => frontendRuntime.operationRegistry.setActive(false));
  const {
    confirmQueue,
    toasts,
    confirmDialog,
    resolveConfirmDialog,
    showToast,
    showErrorToast,
    dismissToast
  } = useGlobalFeedback();
  const runtimeCapabilities = createRuntimeCapabilitiesStore(
    frontendRuntime.sources.runtimeCapabilities);
  const settings = createSettingsStore({
    source: frontendRuntime.sources.appSettings,
    requestClient: frontendRuntime.requestClient
  });
  // Subscribe while visible; unread crashes open the dialog, and management lists all records.
  const crashReports = createCrashReportStore({
    source: frontendRuntime.sources.crashReports,
    active: frontendWork.frontendVisible,
    requestClient: frontendRuntime.requestClient,
    confirm: confirmDialog,
    notifyError: (message) => showToast({ tone: "error", title: currentSettingsText().crashDumps.title, message })
  });
  const productUpdates = createProductUpdateStore({
    source: frontendRuntime.sources.productUpdateStatus,
    active: frontendWork.frontendVisible
  });
  useShellResizeBinding();
  const readContext = useRuntimeReadContext({
    localSystemSource: frontendRuntime.sources.localSystemStatus,
    frontendWork,
    refreshIntervals: settings.frontendRefreshIntervals
  });
  const managementInventory = useManagementInventory({
    sources: frontendRuntime.sources,
    operations: frontendRuntime.operationRegistry,
    inventoryActive: () => frontendWork.isNeeded(frontendWorkIds.managementInventory),
    browserRuntimesActive: () => frontendWork.isNeeded(frontendWorkIds.managementBrowserRuntimes),
    mutablePersistenceEnabled: runtimeCapabilities.mutablePersistenceEnabled
  });
  const management = createManagementStore({
    inventory: managementInventory,
    confirmDialog,
    showToast,
    mutablePersistenceEnabled: runtimeCapabilities.mutablePersistenceEnabled,
    runtimeEffectsEnabled: runtimeCapabilities.runtimeEffectsEnabled,
    operations: frontendRuntime.operationRegistry
  });
  const optimization = createOptimizationStore({
    showToast,
    enabled: runtimeCapabilities.optimizationEnabled,
    reportSource: frontendRuntime.sources.optimizationReports,
    statusSource: frontendRuntime.sources.smartCoordinatorStatus,
    statusActive: () => frontendWork.isNeeded(frontendWorkIds.optimizationSmartStatus),
    statusIntervalMs: () => readContext.readIntervals()?.monitor ?? null,
    reportsActive: () => frontendWork.isNeeded(frontendWorkIds.optimizationReports),
    requestClient: frontendRuntime.requestClient,
    confirmDialog
  });
  const metricSnapshotIntervalMs = createMemo(() =>
    readContext.readIntervals()?.monitor ?? null);
  const resourceTableIntervalMs = createMemo(() =>
    readContext.readIntervals()?.resourceTable ?? null);
  const monitor: MonitorStore = createMonitorStore({
    visibleDashboardCardIds: () =>
      frontendWork.activeVisibilitySourceIds(frontendVisibilitySourceKinds.monitorCard),
    onMetricDependenciesRequested: () => void management.refreshComponents(),
    metricCatalogSource: frontendRuntime.sources.metricCatalog,
    processFieldCatalogSource: frontendRuntime.sources.processFieldCatalog,
    dashboardSettingsSource: frontendRuntime.sources.dashboardSettings,
    metricSnapshotSource: frontendRuntime.sources.metricSnapshot,
    metricFieldCatalogSource: frontendRuntime.sources.metricFieldCatalog,
    metricFieldsSource: frontendRuntime.sources.metricFields,
    resourceMonitorSource: frontendRuntime.sources.resourceMonitor,
    resourceProcessDetailsSource: frontendRuntime.sources.resourceProcessDetails,
    metricSnapshotActive: () =>
      frontendWork.isNeeded(frontendWorkIds.monitorMetrics),
    resourceBarsActive: () =>
      frontendWork.isNeeded(frontendWorkIds.monitorBars),
    resourceTableActive: () =>
      frontendWork.isNeeded(frontendWorkIds.monitorTable),
    metricSnapshotIntervalMs,
    resourceBarsIntervalMs: metricSnapshotIntervalMs,
    resourceTableIntervalMs
  });
  onCleanup(() => {
    unsubscribeTaskCenter();
    optimization.dispose();
    management.dispose();
  });
  function selectManagementSubpage(subpage: ManagementSubpageId) {
    setActivePage("components");
    management.setActiveSubpage(subpage);
  }

  // App owns the selected page; each subpage list owns its selection.
  const detailsNavigation = createDetailsNavigation();
  // Management owns its selected subpage.
  const manageNavigation = createManageNavigation({
    mutablePersistenceEnabled: runtimeCapabilities.mutablePersistenceEnabled
  });
  const openManageSubpage = (subpage: ManageSubpage) => {
    manageNavigation.select(subpage);
    setActivePage("manage");
  };
  // Subscribe to backend notifications while visible.
  // The tray sends Windows notifications; links open the relevant management subpage.
  // Frontend feedback respects each notification category's settings.
  const openSettingsSection = (section: "updates" | "trustedAccess") => {
    settings.setActiveSection(section);
    setActivePage("settings");
  };
  bindBackendNotificationToasts({
    source: frontendRuntime.sources.backendNotifications,
    active: frontendWork.frontendVisible,
    settings: settings.frontendNotifications,
    show: (notification) => {
      switch (notification.kind) {
        case "memory-protection.action": {
          const copy = uiText.memoryProtection;
          const target = copy.recordProcess(notification.name, notification.processId);
          const size = formatBytes(notification.bytesWritten, "storage");
          showToast({
            tone: "warning",
            title: copy.noticeTitle,
            message: notification.targetKind === "process"
              ? copy.noticeProcess(target, size)
              : copy.noticeTree(target, notification.recordCount, size),
            actions: [{ label: copy.noticeOpenRecords, onSelect: () => openManageSubpage("memoryProtection") }]
          });
          return;
        }
        case "cpu-monopoly-protection.action": {
          const copy = uiText.cpuMonopoly;
          const target = copy.process(notification.name, notification.processId);
          showToast({
            tone: notification.outcome === "failed" ? "warning" : "info",
            title: copy.notice.title,
            message: notification.outcome === "normalized"
              ? copy.notice.normalized(target)
              : copy.notice.failed(target, cpuMonopolyActionReasonText(notification.reason ?? "")),
            actions: [{ label: copy.notice.openRecords, onSelect: () => openManageSubpage("cpuMonopoly") }]
          });
          return;
        }
        case "update-available": {
          // Offer immediate application only when this version matches the pending update.
          const text = currentSettingsText();
          const state = productUpdates.status();
          const ready = state?.stage === "pendingApply" && state.targetVersion === notification.version;
          showToast({
            tone: "info",
            title: text.updates.noticeTitle,
            message: ready ? text.updates.noticeReadyMessage(notification.version) : text.updates.noticeMessage(notification.version),
            durationMs: null,
            actions: [
              { label: text.updates.noticeOpenUpdates, onSelect: () => openSettingsSection("updates") },
              ...ready ? [{ label: productUpdateCopy(text.language).apply, onSelect: () => void productUpdates.apply(
                (version) => window.confirm(currentSettingsText().updates.confirmPrepare(version)), currentSettingsText().language) }] : []
            ]
          });
          return;
        }
        case "access-request": {
          const text = currentSettingsText().notifications;
          const program = notification.observedExecutablePath.split(/[\\/]/).pop() || notification.observedExecutablePath;
          showToast({
            tone: "info",
            title: text.accessRequestNoticeTitle,
            message: text.accessRequestNoticeMessage(notification.name, program),
            durationMs: null,
            actions: [{ label: text.accessRequestNoticeOpen, onSelect: () => openSettingsSection("trustedAccess") }]
          });
          return;
        }
        case "crash-report": {
          // The dialog lists unread crashes; this notification links to the full crash history.
          const text = currentSettingsText().crashDumps;
          showToast({
            tone: "warning",
            title: text.noticeTitle,
            message: text.noticeMessage,
            durationMs: null,
            actions: [{ label: text.noticeOpen, onSelect: () => openManageSubpage("crashReports") }]
          });
          return;
        }
      }
    }
  });
  const navigation = createNavigationTree({
    secondary: {
      components: createManagementNavigation({
        components: management.components,
        software: management.software,
        componentsObservation: management.componentsObservation,
        softwareObservation: management.softwareObservation,
        mutablePersistenceEnabled: runtimeCapabilities.mutablePersistenceEnabled,
        selected: management.activeSubpage,
        select: selectManagementSubpage
      }),
      manage: manageNavigation,
      details: detailsNavigation
    }
  });

  const softwareActions = useSoftwareActions({
    management,
    monitor,
    setActivePage,
    openManagementSubpage: selectManagementSubpage,
    confirmDialog,
    showToast,
    showErrorToast,
    mutablePersistenceEnabled: runtimeCapabilities.mutablePersistenceEnabled,
    gpuPlacementEnabled: runtimeCapabilities.gpuPlacementEnabled,
    frontendVisible: frontendWork.frontendVisible,
    runtimeEffectsEnabled: runtimeCapabilities.runtimeEffectsEnabled,
    operations: frontendRuntime.operationRegistry,
    softwareMetadataSource: frontendRuntime.sources.softwareMetadata,
    softwareMetadataLanguage: () => resolveLanguageMode(
      settings.settings().appearance?.language)
  });

  // appTextStore resolves the selected language, loads text, and updates html lang/dir.
  createEffect(() => {
    // appTextStore publishes a persistent error with an explicit retry action.
    void applyLanguage(settings.settings().appearance?.language ?? "system").catch(() => undefined);
  });

  // byteUnits owns conversions and labels for the selected capacity base.
  createEffect(() => {
    applyByteUnitMode(settings.settings().appearance?.byteUnitMode);
  });

  // Page headings and window titles share the navigation tree name.
  const pageTitle = createMemo(() => navigation.page(activePage()).label());
  const preciseGpuPlacementEnabled = createMemo(() => settings.settings().performance?.preciseGpuPlacementEnabled === true);
  const systemUptime = readContext.systemUptime;
  // Use the animation style selected in settings.
  const selectedAnimationStyle = createMemo(() => animationStyle(settings.settings().appearance?.animationStyle).id);

  createEffect(() => {
    const page = activePage();
    document.body.dataset.page = page;
    frontendRuntime.performance.markRouteCommit(page);
  });

  createEffect(() => {
    const subpage = management.activeSubpage();
    const softwareSubpage = !["Dependency", "Support", "BrowserRuntime"].includes(subpage);
    if (softwareSubpage && !runtimeCapabilities.mutablePersistenceEnabled()) {
      management.setActiveSubpage("Dependency");
    }
  });

  createEffect(() => {
    document.body.dataset.frontendFocused = readContext.frontendFocused() ? "yes" : "no";
  });

  createEffect(() => {
    const loadState = settings.loadState();
    if (loadState !== "ready" && loadState !== "refreshing") {
      return;
    }

    const appearance = settings.settings().appearance;
    publishCommittedDocumentAppearance(normalizeUiAppearance(appearance?.uiStyle, appearance?.theme));
  });

  createEffect(() => {
    publishAnimationStyle(animationStyle(selectedAnimationStyle()));
  });

  createEffect(() => {
    document.body.dataset.barColor = normalizeBarColorMode(settings.settings().appearance?.barColorMode);
  });

  createEffect(() => {
    document.body.dataset.resourceBarFrameRate = String(resolveResourceBarFrameRate(settings.settings().appearance));
  });

  createEffect(() => {
    document.body.dataset.fontSmoothing = resolveEffectiveFontSmoothing(
      settings.settings().appearance?.fontSmoothing, selectedAnimationStyle());
  });

  onMount(() => {
    const showCursor = () => document.body.classList.remove("resource-precision-cursor-hidden");
    const escapeHandler = (event: KeyboardEvent) => {
      if (event.key === "Escape" && monitor.resourceSelection()) {
        monitor.clearResourceSelection();
        document.body.classList.remove("resource-precision-cursor-hidden");
      }
    };
    document.addEventListener("mousemove", showCursor);
    document.addEventListener("keydown", escapeHandler);

    onCleanup(() => {
      document.removeEventListener("mousemove", showCursor);
      document.removeEventListener("keydown", escapeHandler);
    });
  });

  async function promptMetricDependency(metric: MetricDefinition, dependency: MetricDependencyState) {
    if (!await confirmDialog({
      title: uiText.appShellExtras.metricNeedsComponent(metric.label, dependency.name),
      message: uiText.appShellExtras.currentState(dependency.stateLabel),
      details: [uiText.appShellExtras.goToComponents],
      tone: "warning"
    })) {
      return;
    }

    monitor.closeMetricModal();
    selectManagementSubpage("Dependency");

    // Open the acquisition dialog directly when the required component is missing.
    const component = management.components().find((item) =>
      (item.definition?.id ?? "").toLowerCase() === dependency.componentId.toLowerCase());
    if (component && !component.installed) {
      void management.installComponent(component);
    }
  }

  function selectPage(page: PageId) {
    if (page === activePage()) {
      return;
    }
    frontendRuntime.performance.markRouteIntent(page);
    setActivePage(page);
  }

  createEffect(() => {
    if (activePage() !== "settings" || settings.activeSection() !== "trustedAccess") {
      setAccessRequestsFocusRequested(false);
    }
  });

  function openAccessRequests() {
    batch(() => {
      setAccessRequestsFocusRequested(true);
      settings.setActiveSection("trustedAccess");
      selectPage("settings");
    });
  }

  return (
    <FrontendWorkProvider controller={frontendWork}>
      <CrashReportProvider store={crashReports}>
      <AccessRequestSnapshotProvider>
      <FrontendReadIntervalsProvider configuration={settings.frontendRefreshIntervals} current={readContext.readIntervals}>
      <>
      <AppShell
        activePage={activePage}
        pageTitle={pageTitle}
        systemUptime={systemUptime}
        navigation={navigation}
        runtimeCapabilities={runtimeCapabilities}
        taskCenterActiveCount={() => taskCenterSnapshot().activeUserCount}
        taskCenterOperationState={() => taskCenterSnapshot().operationState}
        pageFocusTarget={() => accessRequestsFocusRequested() ? trustedAccessFocusTarget() : undefined}
        onPageTargetFocused={(title) => {
          title.scrollIntoView({
            block: "start",
            behavior: animationStyleMoves(selectedAnimationStyle()) ? "smooth" : "auto"
          });
          setAccessRequestsFocusRequested(false);
        }}
        onPageSelect={selectPage}
        onTaskCenterOpen={() => setTaskCenterOpen(true)}
        notices={<>
          <TrustedAccessPendingBanner
            hidden={() => activePage() === "settings" && settings.activeSection() === "trustedAccess"}
            onReview={openAccessRequests}
          />
          <CrashDumpRequestCard
            diagnostics={settings.crashDumps()}
            hidden={activePage() === "manage" && manageNavigation.selected() === "crashReports"}
            onChoose={settings.chooseCrashDumpMode}
          />
          <LanguageLoadNotice />
        </>}
      >
        <AppRoutes
          onTrustedAccessFocusTargetChange={setTrustedAccessFocusTarget}
          animationStyle={selectedAnimationStyle()}
          activePage={activePage}
          detailsTab={detailsNavigation.selected}
          manageSubpage={manageNavigation.selected}
          monitor={monitor}
          management={management}
          optimization={optimization}
          settings={settings}
          productUpdates={productUpdates}
          runtimeCapabilities={runtimeCapabilities}
          highlightedSoftwareId={softwareActions.highlightedSoftwareId}
          onResourceTableSoftwareContextMenu={softwareActions.openResourceTableSoftwareContextMenu}
          contextMenuOpen={softwareActions.contextMenu() !== null}
          onManagementSoftwareContextMenu={softwareActions.openManagementSoftwareContextMenu}
          onOpenSoftwareDetail={softwareActions.openSoftwareDetail}
          softwareDetailOpen={softwareActions.softwareDetail() !== null}
          onOpenSoftwareOverlay={(software) => softwareActions.openSoftwareDetail("software", software, "overlay")}
          onOpenSoftwareSettingsById={(softwareId, softwareName) =>
            void softwareActions.openSoftwareSettingsById(softwareId, softwareName)}
          onNotice={(message) => showToast({
          tone: "info",
          title: uiText.control.title,
          message
        })}
        onInspectOptimizationTarget={(report) => void softwareActions.inspectOptimizationTarget(report)}
        onActionError={showErrorToast}
        onActionSucceeded={(title, message) => showToast({ tone: "success", title, message })}
        confirmDialog={confirmDialog}
        />
      </AppShell>

      <MetricModal
        open={Boolean(monitor.metricModalTarget())}
        catalog={monitor.pickerCatalog()}
        sourceCatalog={monitor.cardCatalog()}
        components={management.components()}
        selectedMetricId={monitor.selectedMetricId()}
        onSelect={monitor.setSelectedMetricId}
        onClose={monitor.closeMetricModal}
        onConfirm={monitor.confirmMetricSelection}
        onDependencyPrompt={promptMetricDependency}
      />
      <SoftwareDetailModal
        detail={softwareActions.softwareDetail()}
        initialTab={softwareActions.softwareDetailTab()}
        notice={softwareActions.softwareDetailNotice()}
        actionInProgress={softwareActions.softwareDetailActionInProgress()}
        gpuPlacementEnabled={runtimeCapabilities.gpuPlacementEnabled()}
        preciseGpuPlacementEnabled={preciseGpuPlacementEnabled() && runtimeCapabilities.gpuPlacementEnabled()}
        runtimeEffectsEnabled={runtimeCapabilities.runtimeEffectsEnabled()}
        mutablePersistenceEnabled={runtimeCapabilities.mutablePersistenceEnabled()}
        gpuPlacementSettings={softwareActions.gpuPlacementSettings()}
        gpuPlacementLoading={softwareActions.gpuPlacementLoading()}
        onClose={softwareActions.closeSoftwareDetail}
        onRemoveManual={() => void softwareActions.removeManualRegistrationFromDetail()}
        onRetryMetadata={() => void softwareActions.retrySoftwareMetadata()}
        onOpenPath={(path) => void softwareActions.openLocalPath(path)}
        onConfirmPortableRoot={() => void softwareActions.confirmPortableRootFromDetail()}
        onSaveGpuPlacementSoftwarePolicy={(policy) => softwareActions.saveGpuPlacementSoftwarePolicyFromDetail(policy)}
        onSaveGpuPlacementProcessPolicy={(policy) => void softwareActions.saveGpuPlacementProcessPolicyFromDetail(policy)}
        confirmDialog={confirmDialog}
      />
      <ManualSoftwareModal
        open={Boolean(management.manualSoftwareKind())}
        kind={management.manualSoftwareKind() ?? "other"}
        sourceSoftware={management.manualSoftwareSource()}
        software={management.software()}
        actionInProgress={management.manualSoftwareActionInProgress()}
        onClose={management.closeManualSoftwareModal}
        onSubmit={(request) => void management.submitManualSoftware(request)}
      />
      <StandardContextMenu
        model={softwareActions.contextMenu()}
        onClose={softwareActions.closeContextMenu}
      />
      <TaskCenterDialog
        open={taskCenterOpen()}
        snapshot={taskCenterSnapshot}
        projection={frontendRuntime.taskCenter}
        diagnosticsEnabled={settings.settings().debug?.debugModeEnabled === true}
        onClose={() => setTaskCenterOpen(false)}
        onCancelError={(error) => showErrorToast(error, uiText.appShellExtras.cancelTaskFailed)}
      />
      <CrashReportDialog store={crashReports} active={frontendWork.frontendVisible()} />
      <ConfirmDialogHost request={confirmQueue()[0] ?? null} onResolve={resolveConfirmDialog} />
      <ToastHost items={toasts()} onDismiss={dismissToast} />
      <TooltipLayer />
      </>
      </FrontendReadIntervalsProvider>
      </AccessRequestSnapshotProvider>
      </CrashReportProvider>
    </FrontendWorkProvider>
  );
}
