import { createEffect, createMemo, createSignal, onCleanup, onMount } from "solid-js";
import { useGlobalFeedback } from "./app/useGlobalFeedback";
import { AppRoutes } from "./app/AppRoutes";
import { AppShell } from "./app/AppShell";
import { usePageRefreshScheduler } from "./app/usePageRefreshScheduler";
import { resolveSelfGpuRuntimePlan } from "./app/selfScheduling/resolveSelfGpuRuntimePlan";
import { useSoftwareActions } from "./app/useSoftwareActions";
import { ConfirmDialogHost, ToastHost } from "./components/AppFeedback";
import { ManualSoftwareModal } from "./components/ManualSoftwareModal";
import { MetricDependencyState, MetricModal } from "./components/MetricModal";
import { SoftwareDetailModal } from "./components/SoftwareDetailModal";
import { StandardContextMenu } from "./components/StandardContextMenu";
import { FrontendWorkProvider } from "./frontendWork/FrontendWorkContext";
import { useFrontendWorkController } from "./frontendWork/useFrontendWorkController";
import { useFrontendRuntime } from "./frontendRuntime/FrontendRuntimeContext";
import { TaskCenterDialog } from "./ui/task-center/TaskCenterDialog";
import type { ManagementSubpageId } from "./management/managementNavigation";
import { publishCommittedDocumentTheme } from "./presentation/documentTheme";
import { createManagementStore } from "./stores/managementStore";
import { createMigrationStore } from "./stores/migrationStore";
import { createMonitorStore } from "./stores/monitorStore";
import { createOptimizationStore } from "./stores/optimizationStore";
import {
  createSettingsStore,
  normalizeBarColorMode,
  normalizeThemeMode,
  resolveAdaptiveBooleanMode,
  resolveLogicRefreshIntervalMs
} from "./stores/settingsStore";
import { createRuntimeCapabilitiesStore } from "./stores/runtimeCapabilitiesStore";
import { resolveLanguageMode } from "./i18n/settingsLanguages";
import { applyLanguage, uiText } from "./text.ts";
import type {
  MetricDefinition,
  PageId
} from "./types";

export default function App() {
  const frontendRuntime = useFrontendRuntime();
  const [activePage, setActivePage] = createSignal<PageId>("monitor");
  const [taskCenterOpen, setTaskCenterOpen] = createSignal(false);
  const [taskCenterSnapshot, setTaskCenterSnapshot] = createSignal(
    frontendRuntime.taskCenter.snapshot);
  const unsubscribeTaskCenter = frontendRuntime.taskCenter.subscribe(
    setTaskCenterSnapshot);
  const frontendWork = useFrontendWorkController();
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
    confirmDialog,
    showToast,
    mutablePersistenceEnabled: runtimeCapabilities.mutablePersistenceEnabled,
    runtimeEffectsEnabled: runtimeCapabilities.runtimeEffectsEnabled,
    operations: frontendRuntime.operationRegistry
  });
  const metricSnapshotIntervalMs = createMemo(() =>
    resolveLogicRefreshIntervalMs(
      "monitor",
      settings.settings().performance?.monitorRefreshIntervalMs));
  const resourceTableIntervalMs = createMemo(() =>
    resolveLogicRefreshIntervalMs(
      "resourceTable",
      settings.settings().performance?.resourceTableRefreshIntervalMs));
  const monitor = createMonitorStore({
    smartMonitoringEnabled: () => resolveAdaptiveBooleanMode(settings.settings().performance?.smartMonitoringMode, true),
    onMetricDependenciesRequested: () => void management.refreshComponents(),
    metricCatalogSource: frontendRuntime.sources.metricCatalog,
    dashboardSettingsSource: frontendRuntime.sources.dashboardSettings,
    metricSnapshotSource: frontendRuntime.sources.metricSnapshot,
    resourceMonitorSource: frontendRuntime.sources.resourceMonitor,
    metricSnapshotActive: () =>
      activePage() === "monitor" && frontendWork.frontendVisible(),
    resourceBarsActive: () =>
      activePage() === "monitor" && frontendWork.frontendVisible(),
    resourceTableActive: () =>
      activePage() === "monitor" && frontendWork.frontendVisible(),
    metricSnapshotIntervalMs,
    resourceBarsIntervalMs: metricSnapshotIntervalMs,
    resourceTableIntervalMs
  });
  const optimization = createOptimizationStore({
    showToast,
    enabled: runtimeCapabilities.optimizationEnabled,
    reportSource: frontendRuntime.sources.optimizationReports,
    requestClient: frontendRuntime.requestClient
  });
  const migration = createMigrationStore({
    confirmDialog,
    showToast,
    refreshSoftware: management.refreshSoftware,
    enabled: runtimeCapabilities.runtimeEffectsEnabled,
    operations: frontendRuntime.operationRegistry,
    tasks: frontendRuntime.taskRegistry
  });
  onCleanup(() => {
    unsubscribeTaskCenter();
    optimization.dispose();
    migration.dispose();
    management.dispose();
  });
  function selectManagementSubpage(subpage: ManagementSubpageId) {
    setActivePage("components");
    management.setActiveSubpage(subpage);
  }

  const softwareActions = useSoftwareActions({
    management,
    monitor,
    migration,
    setActivePage,
    openManagementSubpage: selectManagementSubpage,
    confirmDialog,
    showToast,
    showErrorToast,
    mutablePersistenceEnabled: runtimeCapabilities.mutablePersistenceEnabled,
    gpuPlacementEnabled: runtimeCapabilities.gpuPlacementEnabled,
    runtimeEffectsEnabled: runtimeCapabilities.runtimeEffectsEnabled,
    operations: frontendRuntime.operationRegistry,
    softwareMetadataSource: frontendRuntime.sources.softwareMetadata,
    softwareMetadataLanguage: () => resolveLanguageMode(
      settings.settings().appearance?.language)
  });

  // 界面语言：设置里的选择是唯一来源，解析、载入和 <html lang/dir> 都由 appTextStore 完成。
  createEffect(() => {
    applyLanguage(settings.settings().appearance?.language ?? "system");
  });

  const pageTitle = createMemo(() => {
    if (activePage() === "components") {
      return uiText.page.components;
    }
    if (activePage() === "optimization") {
      return uiText.page.optimization;
    }
    if (activePage() === "details") {
      return uiText.page.details;
    }
    if (activePage() === "settings") {
      return uiText.page.settings;
    }

    return uiText.page.monitor;
  });
  const preciseGpuPlacementEnabled = createMemo(() => settings.settings().performance?.preciseGpuPlacementEnabled !== false);
  const pageRefresh = usePageRefreshScheduler({
    activePage,
    localSystemSource: frontendRuntime.sources.localSystemStatus,
    selfSchedulingSource: frontendRuntime.sources.selfScheduling,
    frontendWork,
    runtimeCapabilities,
    performanceSettings: () => {
      const loadState = settings.loadState();
      return loadState === "ready" || loadState === "refreshing"
        ? settings.settings().performance
        : undefined;
    },
    optimizationMode: optimization.optimizationMode,
    management: {
      activeSubpage: management.activeSubpage,
      refreshState: management.refreshState,
      refreshBrowserRuntimes: management.refreshBrowserRuntimes
    },
    migration: {
      refreshRoots: migration.refreshRoots,
      refreshRecords: migration.refreshRecords,
      refreshSessions: migration.refreshSessions
    },
    optimization: {
      refreshReports: optimization.refreshReports,
      refreshHostManagerStatus: optimization.refreshHostManagerStatus
    }
  });
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
    const softwareSubpage = !["Dependency", "Support", "BrowserRuntime", "Migration"].includes(subpage);
    if ((subpage === "Migration" && !runtimeCapabilities.runtimeEffectsEnabled())
      || (softwareSubpage && !runtimeCapabilities.mutablePersistenceEnabled())) {
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

    publishCommittedDocumentTheme(normalizeThemeMode(settings.settings().appearance?.theme));
  });

  createEffect(() => {
    document.body.dataset.animations = gpuRuntimePlan().animations;
  });

  createEffect(() => {
    document.body.dataset.resourceBarHardwareAcceleration = gpuRuntimePlan().resourceBarHardwareAcceleration ? "on" : "off";
  });

  createEffect(() => {
    document.body.dataset.barColor = normalizeBarColorMode(settings.settings().appearance?.barColorMode);
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
        <AppRoutes
          animationMode={gpuRuntimePlan().animations}
          activePage={activePage}
          monitor={monitor}
          management={management}
          migration={migration}
          optimization={optimization}
          settings={settings}
          runtimeCapabilities={runtimeCapabilities}
          highlightedSoftwareId={softwareActions.highlightedSoftwareId}
          onResourceTableSoftwareContextMenu={softwareActions.openResourceTableSoftwareContextMenu}
          onManagementSoftwareContextMenu={softwareActions.openManagementSoftwareContextMenu}
          onOpenSoftwareDetail={softwareActions.openSoftwareDetail}
          onOpenSoftwareSettingsById={(softwareId, softwareName) =>
            void softwareActions.openSoftwareSettingsById(softwareId, softwareName)}
          onInspectOptimizationTarget={(report) => void softwareActions.inspectOptimizationTarget(report)}
        />
      </AppShell>

      <MetricModal
        open={Boolean(monitor.metricModalTarget())}
        catalog={monitor.catalog()}
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
        migrationRecords={softwareActions.softwareDetailMigrationRecords()}
        actionInProgress={softwareActions.softwareDetailActionInProgress()}
        gpuPlacementEnabled={runtimeCapabilities.gpuPlacementEnabled()}
        preciseGpuPlacementEnabled={preciseGpuPlacementEnabled() && runtimeCapabilities.gpuPlacementEnabled()}
        runtimeEffectsEnabled={runtimeCapabilities.runtimeEffectsEnabled()}
        mutablePersistenceEnabled={runtimeCapabilities.mutablePersistenceEnabled()}
        gpuPlacementSettings={softwareActions.gpuPlacementSettings()}
        gpuPlacementLoading={softwareActions.gpuPlacementLoading()}
        onClose={softwareActions.closeSoftwareDetail}
        onMigrateData={() => void softwareActions.migrateSoftwareDataFromDetail()}
        onMigrateRoot={() => void softwareActions.migrateSoftwareRootFromDetail()}
        onRestoreRecord={(record) => void softwareActions.restoreSoftwareDetailMigrationRecord(record)}
        onRestoreAll={() => void softwareActions.restoreAllSoftwareDetailMigrations()}
        onOpenPath={(path) => void softwareActions.openLocalPath(path)}
        onConfirmPortableRoot={() => void softwareActions.confirmPortableRootFromDetail()}
        onSaveGpuPlacementSoftwarePolicy={(policy) => softwareActions.saveGpuPlacementSoftwarePolicyFromDetail(policy)}
        onSaveGpuPlacementProcessPolicy={(policy) => void softwareActions.saveGpuPlacementProcessPolicyFromDetail(policy)}
      />
      <ManualSoftwareModal
        open={Boolean(management.manualSoftwareKind())}
        kind={management.manualSoftwareKind() ?? "Other"}
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
    </FrontendWorkProvider>
  );
}
