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
import type {
  MetricDefinition,
  PageId
} from "./types";

// 软件详情与任务中心按需打开，各自一个分包；首屏渲染时即开始载入，打开时已就绪。
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
  // 崩溃记录（docs/crash_dumps_2026-10-31.md）：界面可见时订阅；有未读记录时弹出崩溃提示弹窗，设置 · 系统集成列出全部记录。
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

  // 分页树（docs/navigation_tree_2026-10-25.md）：一级选中项是 activePage，二级选中项各有各的所有者。
  const detailsNavigation = createDetailsNavigation({
    optimizationEnabled: runtimeCapabilities.optimizationEnabled,
    mutablePersistenceEnabled: runtimeCapabilities.mutablePersistenceEnabled
  });
  // 后端通知（docs/notifications_2026-10-29.md“前端”）：界面可见时订阅，新通知按种类用界面提示显示；
  // Windows 通知由托盘发。入口跳到详细信息的内存保护记录。
  // 后端通知的界面提示（docs/notifications_2026-10-29.md“前端”）：界面打开时订阅，按种类和这一类的设置提示。
  const openSettingsSection = (section: "updates" | "trustedAccess" | "systemIntegration") => {
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
            actions: [{ label: copy.noticeOpenRecords, onSelect: () => {
              detailsNavigation.select("memoryProtection");
              setActivePage("details");
            } }]
          });
          return;
        }
        case "update-available": {
          // 待应用的正是这个版本时（按此刻的更新状态），提示写“已就绪”并给出立即应用。
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
          // 未读的记录同时由崩溃提示弹窗列出；提示只带到设置 · 系统集成的崩溃记录。
          const text = currentSettingsText().crashDumps;
          showToast({
            tone: "warning",
            title: text.noticeTitle,
            message: text.noticeMessage,
            durationMs: null,
            actions: [{ label: text.noticeOpen, onSelect: () => openSettingsSection("systemIntegration") }]
          });
          return;
        }
      }
    }
  });
  const navigation = createNavigationTree({
    optimizationVisible: runtimeCapabilities.optimizationEnabled,
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

  // 界面语言：设置里的选择是唯一来源，解析、载入和 <html lang/dir> 都由 appTextStore 完成。
  createEffect(() => {
    // appTextStore publishes a persistent error with an explicit retry action.
    void applyLanguage(settings.settings().appearance?.language ?? "system").catch(() => undefined);
  });

  // 容量进制：设置里的选择是唯一来源，换算与单位标签都由 byteUnits 统一给出。
  createEffect(() => {
    applyByteUnitMode(settings.settings().appearance?.byteUnitMode);
  });

  // 页名的唯一来源是分页树：页标题（读屏、焦点落点）和窗口标题都读它。
  const pageTitle = createMemo(() => navigation.page(activePage()).label());
  const preciseGpuPlacementEnabled = createMemo(() => settings.settings().performance?.preciseGpuPlacementEnabled === true);
  const systemUptime = readContext.systemUptime;
  // 动画风格只按用户在设置里的选择（docs/animation_style_2026-10-26.md）。
  const selectedAnimationStyle = createMemo(() => animationStyle(settings.settings().appearance?.animationStyle).id);

  createEffect(() => {
    const page = activePage();
    document.body.dataset.page = page;
    frontendRuntime.performance.markRouteCommit(page);
  });

  createEffect(() => {
    if (activePage() === "optimization" && !runtimeCapabilities.optimizationEnabled()) {
      setActivePage("monitor");
    }
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

    // 缺的组件如果还没装，直接把标准获取弹窗开出来，不让用户自己在列表里找。
    const component = management.components().find((item) =>
      (item.definition?.id ?? "").toLowerCase() === dependency.componentId.toLowerCase());
    if (component && !component.installed) {
      void management.installComponent(component);
    }
  }

  function selectPage(page: PageId) {
    if (page === "optimization" && !runtimeCapabilities.optimizationEnabled()) {
      return;
    }
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
            hidden={activePage() === "settings" && settings.activeSection() === "systemIntegration"}
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
