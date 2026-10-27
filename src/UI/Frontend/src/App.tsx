import { FrontendReadIntervalsProvider } from "./app/FrontendReadIntervalsContext.tsx";
import { batch, createEffect, createMemo, createSignal, lazy, onCleanup, onMount } from "solid-js";
import { useGlobalFeedback } from "./app/useGlobalFeedback";
import { AppRoutes } from "./app/AppRoutes";
import { AppShell } from "./app/AppShell";
import { useRuntimeReadContext } from "./app/useRuntimeReadContext";
import { useShellResizeBinding } from "./app/useShellResizeBinding";
import { useManagementInventory } from "./features/management/useManagementInventory";
import { resolveSelfGpuRuntimePlan } from "./app/selfScheduling/resolveSelfGpuRuntimePlan";
import { createAnimationStyleSelection } from "./app/selfScheduling/createAnimationStyleSelection";
import { subscribeInteractionInput } from "./interactions/solid/lodestone.ts";
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
  normalizeAutoDisableAnimations,
  normalizeBarColorMode
} from "./stores/settingsStore";
import { createRuntimeCapabilitiesStore } from "./stores/runtimeCapabilitiesStore";
import { AccessRequestSnapshotProvider } from "./features/trustedAccess/AccessRequestSnapshotContext.tsx";
import { TrustedAccessPendingBanner } from "./features/settings/components/TrustedAccessPendingBanner.tsx";
import { createProductUpdateStore } from "./stores/productUpdateStore";
import { bindProductUpdateNotice } from "./stores/productUpdateNotice.ts";
import { productUpdateCopy } from "./features/settings/productUpdatePresentation.ts";
import { resolveLanguageMode } from "./i18n/settingsLanguages";
import { applyLanguage, currentSettingsText, uiText } from "./text.ts";
import { LanguageLoadNotice } from "./i18n/LanguageLoadNotice";
import { applyByteUnitMode } from "./presentation/byteUnits.ts";
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
  const productUpdates = createProductUpdateStore({
    source: frontendRuntime.sources.productUpdateStatus,
    active: frontendWork.frontendVisible
  });
  // 发现新版本时界面内提示一次（docs: update-notice 合同“提示一次”）；入口跳到更新与版本，待应用时可立即应用。
  bindProductUpdateNotice({
    status: productUpdates.status,
    notifications: settings.frontendNotifications,
    record: settings.recordUpdateInAppNotified,
    show: (state) => {
      const text = currentSettingsText();
      const ready = state.stage === "pendingApply" && state.targetVersion === state.availableVersion;
      showToast({
        tone: "info",
        title: text.updates.noticeTitle,
        message: ready ? text.updates.noticeReadyMessage(state.availableVersion) : text.updates.noticeMessage(state.availableVersion),
        durationMs: null,
        actions: [
          { label: text.updates.noticeOpenUpdates, onSelect: () => {
            settings.setActiveSection("updates");
            setActivePage("settings");
          } },
          ...ready ? [{ label: productUpdateCopy(text.language).apply, onSelect: () => void productUpdates.apply(
            (version) => window.confirm(currentSettingsText().updates.confirmPrepare(version)), currentSettingsText().language) }] : []
        ]
      });
    }
  });
  useShellResizeBinding();
  const readContext = useRuntimeReadContext({
    localSystemSource: frontendRuntime.sources.localSystemStatus,
    selfSchedulingSource: frontendRuntime.sources.selfScheduling,
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
    statusIntervalMs: () => readContext.cpuRuntimePlan()?.monitor ?? null,
    reportsActive: () => frontendWork.isNeeded(frontendWorkIds.optimizationReports),
    requestClient: frontendRuntime.requestClient
  });
  const metricSnapshotIntervalMs = createMemo(() =>
    readContext.cpuRuntimePlan()?.monitor ?? null);
  const resourceTableIntervalMs = createMemo(() =>
    readContext.cpuRuntimePlan()?.resourceTable ?? null);
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
  const currentSelfScheduling = readContext.selfScheduling;
  const animationSelection = createAnimationStyleSelection({
    style: () => animationStyle(settings.settings().appearance?.animationStyle).id,
    autoDisable: () => normalizeAutoDisableAnimations(settings.settings().appearance?.autoDisableAnimations),
    foreground: readContext.foregroundInteractive,
    gpuGrade: () => currentSelfScheduling()?.gpuGrade ?? "normal",
    now: () => performance.now()
  });
  onCleanup(subscribeInteractionInput(animationSelection.recordInput));
  const gpuRuntimePlan = createMemo(() => resolveSelfGpuRuntimePlan(
    settings.settings().appearance,
    animationSelection.effectiveStyle()));

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
    publishAnimationStyle(animationStyle(animationSelection.effectiveStyle()));
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
      <AccessRequestSnapshotProvider>
      <FrontendReadIntervalsProvider configuration={settings.frontendRefreshIntervals} current={readContext.cpuRuntimePlan}>
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
            behavior: animationStyleMoves(gpuRuntimePlan().animations) ? "smooth" : "auto"
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
          <LanguageLoadNotice />
        </>}
      >
        <AppRoutes
          onTrustedAccessFocusTargetChange={setTrustedAccessFocusTarget}
          animationStyle={gpuRuntimePlan().animations}
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
      <ConfirmDialogHost request={confirmQueue()[0] ?? null} onResolve={resolveConfirmDialog} />
      <ToastHost items={toasts()} onDismiss={dismissToast} />
      <TooltipLayer />
      </>
      </FrontendReadIntervalsProvider>
      </AccessRequestSnapshotProvider>
    </FrontendWorkProvider>
  );
}
