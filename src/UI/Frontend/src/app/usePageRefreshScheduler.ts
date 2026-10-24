import { createEffect, createMemo, createSignal, onCleanup, onMount } from "solid-js";
import type { Accessor } from "solid-js";
import type { LocalSystemStatus } from "../data/localSystem/localSystemStatusApi";
import type { ResourceManagerSelfSchedulingSnapshot } from "../data/selfScheduling/resourceManagerSelfSchedulingApi";
import { frontendWorkIds, type FrontendWorkId } from "../frontendWork/frontendWorkIds.ts";
import type { FrontendWorkController } from "../frontendWork/useFrontendWorkController";
import type { PushValueSource } from "../frontendRuntime/push/PushValueSourceFamily";
import type { RuntimeCapabilitiesStore } from "../stores/runtimeCapabilitiesStore";
import {
  browserRuntimeManagementSubpageId,
  type ManagementSubpageId
} from "../features/management/managementNavigation.ts";
import { resolveSelfCpuRuntimePlan } from "./selfScheduling/resolveSelfCpuRuntimePlan.ts";
import type { FrontendRefreshIntervals, PageId } from "../types";
import { beginShellWindowResize, endShellWindowResize, subscribeShellHostMessages } from "../utils.ts";
import type { OperationRegistry } from "../frontendRuntime/operations/OperationRegistry";

type RefreshAction = () => void | Promise<unknown>;
type RefreshActionWithForce = (force: boolean) => void | Promise<unknown>;

interface PageRefreshSchedulerOptions {
  operations: Pick<OperationRegistry, "subscribeInvalidations">;
  activePage: Accessor<PageId>;
  localSystemSource: PushValueSource<LocalSystemStatus>;
  selfSchedulingSource: PushValueSource<ResourceManagerSelfSchedulingSnapshot>;
  frontendWork: Pick<FrontendWorkController, "frontendVisible" | "activeWorkIds" | "isNeeded">;
  runtimeCapabilities: Pick<RuntimeCapabilitiesStore, "runtimeEffectsEnabled" | "optimizationEnabled">;
  refreshIntervals: Accessor<FrontendRefreshIntervals | null>;
  management: {
    activeSubpage: Accessor<ManagementSubpageId>;
    refreshState: RefreshActionWithForce;
    refreshBrowserRuntimes: RefreshActionWithForce;
    refreshComponents: RefreshAction;
    refreshSoftware: RefreshAction;
  };
  optimization: {
    refreshReports: RefreshActionWithForce;
    refreshHostManagerStatus: RefreshAction;
  };
}

export function usePageRefreshScheduler(options: PageRefreshSchedulerOptions) {
  const [frontendFocused, setFrontendFocused] = createSignal(document.hasFocus());
  const [selfScheduling, setSelfScheduling] =
    createSignal<ResourceManagerSelfSchedulingSnapshot | null>(null);
  const [localSystemStatus, setLocalSystemStatus] =
    createSignal<LocalSystemStatus | null>(null);
  const frontendVisible = options.frontendWork.frontendVisible;
  const performanceSettingsAvailable = createMemo(() => options.refreshIntervals() !== null);
  // Settings publications also change for language and other unrelated edits.
  // Only a different cadence may replace this observation's subscription.
  const selfSchedulingIntervalMs = createMemo(
    () => options.refreshIntervals()?.foreground.monitor ?? null);
  const foregroundInteractive = createMemo(() => frontendVisible() && frontendFocused());
  const cpuRuntimePlan = createMemo(() => resolveSelfCpuRuntimePlan(
    options.refreshIntervals(),
    selfScheduling()?.cpuGrade ?? "normal",
    foregroundInteractive()), null, { equals: (left, right) => left?.monitor === right?.monitor
      && left?.resourceTable === right?.resourceTable && left?.localSystem === right?.localSystem });
  const currentLocalSystemRefreshIntervalMs = createMemo(
    () => cpuRuntimePlan()?.localSystem ?? null);
  const systemUptime = createMemo(() => {
    const status = localSystemStatus();
    return status ? formatUptimeSeconds(status.uptimeSeconds) : "--";
  });
  let resumeRefreshFrame = 0;
  let resumeRefreshTimer = 0;
  let previousFrontendVisible = frontendVisible();
  let previousActiveWorkIds: readonly FrontendWorkId[] = [];
  let previousRouteIdentity = "";

  createEffect(() => {
    if (!frontendVisible() || options.activePage() !== "components") return;
    const unsubscribe = options.operations.subscribeInvalidations((invalidation) => {
      if (invalidation.targets.includes("components")
        && options.frontendWork.isNeeded(frontendWorkIds.managementInventory)) {
        void options.management.refreshComponents();
      }
      if (invalidation.targets.includes("software")
        && options.frontendWork.isNeeded(frontendWorkIds.managementInventory)) {
        void options.management.refreshSoftware();
      }
      if (invalidation.targets.includes("dependencies")
        && options.frontendWork.isNeeded(frontendWorkIds.managementBrowserRuntimes)) {
        void options.management.refreshBrowserRuntimes(false);
      }
      if (invalidation.targets.includes("components")
        && options.frontendWork.isNeeded(frontendWorkIds.managementBrowserRuntimes)) {
        void options.management.refreshComponents();
      }
    });
    onCleanup(unsubscribe);
  });

  createEffect(() => {
    // The scheduling observation cannot derive its own demand from its value:
    // releasing that value to change cadence would change the selected profile.
    const intervalMs = selfSchedulingIntervalMs();
    if (!frontendVisible() || intervalMs == null) {
      return;
    }
    const unsubscribe = options.selfSchedulingSource.subscribeSnapshot(
      intervalMs,
      (snapshot) => setSelfScheduling(snapshot.data));
    onCleanup(() => { unsubscribe(); setSelfScheduling(null); });
  });

  // 本机状态和其他数据源一样，窗口隐藏即停止订阅，重新显示时再订阅。
  createEffect(() => {
    if (!performanceSettingsAvailable()) {
      return;
    }
    const intervalMs = currentLocalSystemRefreshIntervalMs();
    if (!frontendVisible() || intervalMs === null) {
      return;
    }
    const unsubscribe = options.localSystemSource.subscribeSnapshot(
      intervalMs,
      (snapshot) => setLocalSystemStatus(snapshot.data));
    onCleanup(() => { unsubscribe(); setLocalSystemStatus(null); });
  });

  createEffect(() => {
    const visible = frontendVisible();
    const nextActiveWorkIds = options.frontendWork.activeWorkIds();
    const routeIdentity = options.activePage() === "components"
      ? `components:${options.management.activeSubpage()}`
      : options.activePage();
    const hasNewDemand = nextActiveWorkIds.some(
      (workId) => !previousActiveWorkIds.includes(workId));
    if (visible && (
      !previousFrontendVisible
      || routeIdentity !== previousRouteIdentity
      || hasNewDemand
    )) {
      scheduleVisiblePageRefresh();
    }
    previousFrontendVisible = visible;
    previousActiveWorkIds = nextActiveWorkIds;
    previousRouteIdentity = routeIdentity;
  });

  onMount(() => {
    const focusHandler = () => setFrontendFocused(true);
    const blurHandler = () => setFrontendFocused(false);
    window.addEventListener("focus", focusHandler);
    window.addEventListener("blur", blurHandler);
    const unsubscribeHostMessages = subscribeShellHostMessages((message) => {
      if (message === "host.window-resize:start") {
        beginShellWindowResize();
        return;
      }

      if (message === "host.window-resize:end") {
        endShellWindowResize();
      }
    });

    onCleanup(() => {
      window.removeEventListener("focus", focusHandler);
      window.removeEventListener("blur", blurHandler);
      unsubscribeHostMessages();
      cancelScheduledVisiblePageRefresh();
    });
  });

  function scheduleVisiblePageRefresh() {
    cancelScheduledVisiblePageRefresh();
    resumeRefreshFrame = window.requestAnimationFrame(() => {
      resumeRefreshFrame = window.requestAnimationFrame(() => {
        resumeRefreshFrame = 0;
        resumeRefreshTimer = window.setTimeout(() => {
          resumeRefreshTimer = 0;
          if (frontendVisible()) {
            void refreshVisiblePageData();
          }
        }, 60);
      });
    });
  }

  function cancelScheduledVisiblePageRefresh() {
    if (resumeRefreshFrame) {
      window.cancelAnimationFrame(resumeRefreshFrame);
      resumeRefreshFrame = 0;
    }
    if (resumeRefreshTimer) {
      window.clearTimeout(resumeRefreshTimer);
      resumeRefreshTimer = 0;
    }
  }

  async function refreshVisiblePageData() {
    if (options.activePage() === "monitor") {
      return;
    }

    if (options.activePage() === "components") {
      if (options.management.activeSubpage() === browserRuntimeManagementSubpageId) {
        if (options.frontendWork.isNeeded(frontendWorkIds.managementBrowserRuntimes)) {
          // The runtime page also shows its install component.
          await Promise.all([
            options.management.refreshBrowserRuntimes(false),
            options.management.refreshComponents()
          ]);
        }
      } else if (options.frontendWork.isNeeded(frontendWorkIds.managementInventory)) {
        await options.management.refreshState(false);
      }
      return;
    }

    if (options.activePage() === "optimization" && options.runtimeCapabilities.optimizationEnabled()) {
      const requests: Promise<unknown>[] = [];
      if (options.frontendWork.isNeeded(frontendWorkIds.optimizationReports)) {
        requests.push(Promise.resolve(options.optimization.refreshReports(false)));
      }
      if (options.frontendWork.isNeeded(frontendWorkIds.optimizationSmartStatus)) {
        requests.push(Promise.resolve(options.optimization.refreshHostManagerStatus()));
      }
      await Promise.all(requests);
    }
  }

  return {
    systemUptime,
    frontendFocused,
    foregroundInteractive,
    selfScheduling,
    cpuRuntimePlan,
    refreshVisiblePageData
  };
}

function formatUptimeSeconds(value: number) {
  const totalSeconds = Math.max(0, Math.floor(value));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}
