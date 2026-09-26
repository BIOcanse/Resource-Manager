import { createEffect, createMemo, createSignal, onCleanup, onMount } from "solid-js";
import type { Accessor } from "solid-js";
import type { LocalSystemStatus } from "../data/localSystem/localSystemStatusApi";
import type { ResourceManagerSelfSchedulingSnapshot } from "../data/selfScheduling/resourceManagerSelfSchedulingApi";
import { frontendWorkIds, type FrontendWorkId } from "../frontendWork/frontendWorkIds";
import type { FrontendWorkController } from "../frontendWork/useFrontendWorkController";
import type { PushValueSource } from "../frontendRuntime/push/PushValueSourceFamily";
import type { RuntimeCapabilitiesStore } from "../stores/runtimeCapabilitiesStore";
import {
  browserRuntimeManagementSubpageId,
  type ManagementSubpageId
} from "../features/management/managementNavigation";
import { resolveSelfCpuRuntimePlan } from "./selfScheduling/resolveSelfCpuRuntimePlan";
import type { AppOptimizationMode, AppPerformanceSettings, PageId } from "../types";
import { beginShellWindowResize, endShellWindowResize, subscribeShellHostMessages } from "../utils";

type RefreshAction = () => void | Promise<unknown>;
type RefreshActionWithForce = (force: boolean) => void | Promise<unknown>;
const selfSchedulingSubscriptionIntervalMs = 5_000;

interface PageRefreshSchedulerOptions {
  activePage: Accessor<PageId>;
  localSystemSource: PushValueSource<LocalSystemStatus>;
  selfSchedulingSource: PushValueSource<ResourceManagerSelfSchedulingSnapshot>;
  frontendWork: Pick<FrontendWorkController, "frontendVisible" | "activeWorkIds" | "isNeeded">;
  runtimeCapabilities: Pick<RuntimeCapabilitiesStore, "runtimeEffectsEnabled" | "optimizationEnabled">;
  performanceSettings: Accessor<AppPerformanceSettings | undefined>;
  optimizationMode: Accessor<AppOptimizationMode>;
  management: {
    activeSubpage: Accessor<ManagementSubpageId>;
    refreshState: RefreshActionWithForce;
    refreshBrowserRuntimes: RefreshActionWithForce;
  };
  migration: {
    refreshRoots: RefreshAction;
    refreshRecords: RefreshAction;
    refreshSessions: RefreshAction;
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
  const foregroundInteractive = createMemo(() => frontendVisible() && frontendFocused());
  const cpuRuntimePlan = createMemo(() => resolveSelfCpuRuntimePlan(
    options.performanceSettings(),
    options.optimizationMode(),
    selfScheduling()?.cpuGrade ?? "normal",
    frontendVisible(),
    foregroundInteractive()));
  const frontendRefreshPaused = createMemo(() => cpuRuntimePlan().frontendRefreshPaused);
  const currentLocalSystemRefreshIntervalMs = createMemo(
    () => cpuRuntimePlan().refreshIntervalMs.localSystem);
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
    if (!frontendVisible()) {
      return;
    }
    const unsubscribe = options.selfSchedulingSource.subscribe(
      selfSchedulingSubscriptionIntervalMs,
      setSelfScheduling);
    onCleanup(unsubscribe);
  });

  createEffect(() => {
    if (!options.performanceSettings()) {
      return;
    }
    const intervalMs = currentLocalSystemRefreshIntervalMs();
    if (frontendRefreshPaused()) {
      return;
    }
    const unsubscribe = options.localSystemSource.subscribe(
      intervalMs,
      setLocalSystemStatus);
    onCleanup(unsubscribe);
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
          await options.management.refreshBrowserRuntimes(false);
        }
      } else if (options.management.activeSubpage() === "Migration") {
        if (!options.runtimeCapabilities.runtimeEffectsEnabled()) {
          return;
        }
        const requests: Promise<unknown>[] = [];
        if (options.frontendWork.isNeeded(frontendWorkIds.migrationRoots)) {
          requests.push(Promise.resolve(options.migration.refreshRoots()));
        }
        if (options.frontendWork.isNeeded(frontendWorkIds.migrationRecords)) {
          requests.push(Promise.resolve(options.migration.refreshRecords()));
        }
        if (options.frontendWork.isNeeded(frontendWorkIds.migrationSessions)) {
          requests.push(Promise.resolve(options.migration.refreshSessions()));
        }
        await Promise.all(requests);
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
