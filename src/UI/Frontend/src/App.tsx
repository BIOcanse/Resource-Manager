import { FrontendReadIntervalsProvider } from "./app/FrontendReadIntervalsContext.tsx";
import { createEffect, createMemo, createSignal, lazy, onCleanup, onMount } from "solid-js";
import { useGlobalFeedback } from "./app/useGlobalFeedback";
import { AppRoutes } from "./app/AppRoutes";
import { AppShell } from "./app/AppShell";
import { usePageRefreshScheduler } from "./app/usePageRefreshScheduler";
import { resolveSelfGpuRuntimePlan } from "./app/selfScheduling/resolveSelfGpuRuntimePlan";
import { useSoftwareActions } from "./app/useSoftwareActions";
import { ConfirmDialogHost, ToastHost } from "./components/AppFeedback";
import { ManualSoftwareModal } from "./features/management/components/ManualSoftwareModal";
import { MetricDependencyState, MetricModal } from "./features/monitor/MetricModal";
import { StandardContextMenu } from "./components/StandardContextMenu";
import { FrontendWorkProvider } from "./frontendWork/FrontendWorkContext";
import { useFrontendWorkController } from "./frontendWork/useFrontendWorkController";
import { frontendWorkIds } from "./frontendWork/frontendWorkIds";
import { useFrontendRuntime } from "./frontendRuntime/FrontendRuntimeContext";
import type { ManagementSubpageId } from "./features/management/managementNavigation";
import { publishCommittedDocumentAppearance } from "./presentation/documentAppearance";
import { normalizeUiAppearance } from "./presentation/uiStyles";
import { resolveResourceBarFrameRate } from "./presentation/resourceBarFrameRate.ts";
import { createManagementStore } from "./stores/managementStore";
import { createMonitorStore, type MonitorStore } from "./stores/monitorStore";
import { createOptimizationStore } from "./stores/optimizationStore";
import {
  createSettingsStore,
  normalizeBarColorMode
} from "./stores/settingsStore";
import { createRuntimeCapabilitiesStore } from "./stores/runtimeCapabilitiesStore";
import { resolveLanguageMode } from "./i18n/settingsLanguages";
import { applyLanguage, uiText } from "./text.ts";
import { LanguageLoadNotice } from "./i18n/LanguageLoadNotice";
import { applyByteUnitMode } from "./presentation/byteUnits.ts";
import { applyReadingSourceMode } from "./presentation/readingSources.ts";
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
  const management = createManagementStore({
    activePage,
    inventoryRevisionSource: frontendRuntime.sources.managementInventoryRevision,
    inventoryActive: () => frontendWork.frontendVisible() && frontendWork.isNeeded(frontendWorkIds.managementInventory),
    browserRuntimesActive: () => frontendWork.frontendVisible() && frontendWork.isNeeded(frontendWorkIds.managementBrowserRuntimes),
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
    statusIntervalMs: () => pageRefresh.cpuRuntimePlan()?.monitor ?? null,
    reportsActive: () => frontendWork.isNeeded(frontendWorkIds.optimizationReports),
    requestClient: frontendRuntime.requestClient
  });
  // 后端已展开刷新间隔；这里按本程序 CPU 档位和本地前台状态选择已发布的上下文。
  // 调度器先于监控存储创建，所有读数消费者直接使用选中的间隔。
  const pageRefresh = usePageRefreshScheduler({
    operations: frontendRuntime.operationRegistry,
    activePage,
    localSystemSource: frontendRuntime.sources.localSystemStatus,
    selfSchedulingSource: frontendRuntime.sources.selfScheduling,
    frontendWork,
    runtimeCapabilities,
    refreshIntervals: settings.frontendRefreshIntervals,
    management: {
      activeSubpage: management.activeSubpage,
      refreshState: management.refreshState,
      refreshComponents: management.refreshComponents,
      refreshSoftware: management.refreshSoftware,
      refreshBrowserRuntimes: management.refreshBrowserRuntimes
    },
    optimization: {
      refreshReports: optimization.refreshReports,
      refreshHostManagerStatus: optimization.refreshHostManagerStatus
    }
  });
  const metricSnapshotIntervalMs = createMemo(() =>
    pageRefresh.cpuRuntimePlan()?.monitor ?? null);
  const resourceTableIntervalMs = createMemo(() =>
    pageRefresh.cpuRuntimePlan()?.resourceTable ?? null);
  const monitor: MonitorStore = createMonitorStore({
    smartMonitoringEnabled: () => settings.settings().performance?.smartMonitoringMode === "enabled",
    onMetricDependenciesRequested: () => void management.refreshComponents(),
    metricCatalogSource: frontendRuntime.sources.metricCatalog,
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

  // 读数来源：设置里的选择是唯一来源，选择器、卡片和设备详情都由 readingSources 统一筛选和加标记。
  createEffect(() => {
    applyReadingSourceMode(settings.settings().appearance?.readingSourceMode);
  });

  const pageTitle = createMemo(() => {
    if (activePage() === "components") {
      return uiText.page.components;
    }
    if (activePage() === "optimization") {
      return uiText.page.optimization;
    }
    if (activePage() === "diskUsage") {
      return uiText.page.diskUsage;
    }
    if (activePage() === "control") {
      return uiText.page.control;
    }
    if (activePage() === "details") {
      return uiText.page.details;
    }
    if (activePage() === "settings") {
      return uiText.page.settings;
    }

    return uiText.page.monitor;
  });
  const preciseGpuPlacementEnabled = createMemo(() => settings.settings().performance?.preciseGpuPlacementEnabled === true);
  const systemUptime = pageRefresh.systemUptime;
  const currentSelfScheduling = pageRefresh.selfScheduling;
  const gpuRuntimePlan = createMemo(() => resolveSelfGpuRuntimePlan(
    settings.settings().appearance,
    currentSelfScheduling()?.gpuGrade ?? "normal",
    pageRefresh.foregroundInteractive()));

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
    document.body.dataset.selfCpuGrade = currentSelfScheduling()?.cpuGrade ?? "normal";
    document.body.dataset.selfGpuGrade = currentSelfScheduling()?.gpuGrade ?? "normal";
  });

  createEffect(() => {
    document.body.dataset.frontendFocused = pageRefresh.frontendFocused() ? "yes" : "no";
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
    document.body.dataset.animations = gpuRuntimePlan().animations;
  });

  createEffect(() => {
    document.body.dataset.barColor = normalizeBarColorMode(settings.settings().appearance?.barColorMode);
  });

  createEffect(() => {
    document.body.dataset.resourceBarFrameRate = String(resolveResourceBarFrameRate(settings.settings().appearance));
  });

  createEffect(() => {
    document.body.dataset.fontSmoothing = gpuRuntimePlan().fontSmoothing;
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

  return (
    <FrontendWorkProvider controller={frontendWork}>
      <FrontendReadIntervalsProvider configuration={settings.frontendRefreshIntervals} current={pageRefresh.cpuRuntimePlan}>
      <>
      <AppShell
        activePage={activePage}
        pageTitle={pageTitle}
        systemUptime={systemUptime}
        management={management}
        runtimeCapabilities={runtimeCapabilities}
        taskCenterActiveCount={() => taskCenterSnapshot().activeUserCount}
        taskCenterOperationState={() => taskCenterSnapshot().operationState}
        onPageSelect={selectPage}
        onManagementSubpageSelect={selectManagementSubpage}
        onTaskCenterOpen={() => setTaskCenterOpen(true)}
      >
        <LanguageLoadNotice />
        <AppRoutes
          animationMode={gpuRuntimePlan().animations}
          activePage={activePage}
          monitor={monitor}
          management={management}
          optimization={optimization}
          settings={settings}
          runtimeCapabilities={runtimeCapabilities}
          highlightedSoftwareId={softwareActions.highlightedSoftwareId}
          onResourceTableSoftwareContextMenu={softwareActions.openResourceTableSoftwareContextMenu}
          contextMenuOpen={softwareActions.contextMenu() !== null}
          onManagementSoftwareContextMenu={softwareActions.openManagementSoftwareContextMenu}
          onOpenSoftwareDetail={softwareActions.openSoftwareDetail}
          onOpenSoftwareSettingsById={(softwareId, softwareName) =>
            void softwareActions.openSoftwareSettingsById(softwareId, softwareName)}
          onNotice={(message) => showToast({
          tone: "info",
          title: uiText.control.title,
          message
        })}
        onInspectOptimizationTarget={(report) => void softwareActions.inspectOptimizationTarget(report)}
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
        onUnavailable={(message) => showToast({
          tone: "info",
          title: uiText.appShellExtras.noRunnableAction,
          message
        })}
      />
      <TaskCenterDialog
        open={taskCenterOpen()}
        snapshot={taskCenterSnapshot}
        projection={frontendRuntime.taskCenter}
        diagnosticsEnabled={settings.settings().debug?.debugModeEnabled === true}
        onClose={() => setTaskCenterOpen(false)}
        onCancelError={(error) => showErrorToast(error, uiText.appShellExtras.cancelTaskFailed)}
      />
      <ConfirmDialogHost request={confirmQueue()[0] ?? null} onResolve={resolveConfirmDialog} />
      <ToastHost items={toasts()} onDismiss={dismissToast} />
      </>
      </FrontendReadIntervalsProvider>
    </FrontendWorkProvider>
  );
}
