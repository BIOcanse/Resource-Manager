import { createEffect, createMemo, createSignal, onCleanup, onMount } from "solid-js";
import type { Accessor } from "solid-js";
import type { LocalSystemStatus } from "../data/localSystem/localSystemStatusApi";
import type { ResourceManagerSelfSchedulingSnapshot } from "../data/selfScheduling/resourceManagerSelfSchedulingApi";
import type { FrontendWorkController } from "../frontendWork/useFrontendWorkController";
import type { PushValueSource } from "../frontendRuntime/push/PushValueSourceFamily";
import { resolveSelfCpuRuntimePlan } from "./selfScheduling/resolveSelfCpuRuntimePlan.ts";
import type { FrontendRefreshIntervals } from "../types";

interface RuntimeReadContextOptions {
  localSystemSource: PushValueSource<LocalSystemStatus>;
  selfSchedulingSource: PushValueSource<ResourceManagerSelfSchedulingSnapshot>;
  frontendWork: Pick<FrontendWorkController, "frontendVisible">;
  refreshIntervals: Accessor<FrontendRefreshIntervals | null>;
}

export function useRuntimeReadContext(options: RuntimeReadContextOptions) {
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
    onCleanup(unsubscribe);
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
    onCleanup(unsubscribe);
  });

  onMount(() => {
    const focusHandler = () => setFrontendFocused(true);
    const blurHandler = () => setFrontendFocused(false);
    window.addEventListener("focus", focusHandler);
    window.addEventListener("blur", blurHandler);
    onCleanup(() => {
      window.removeEventListener("focus", focusHandler);
      window.removeEventListener("blur", blurHandler);
    });
  });

  return {
    systemUptime,
    frontendFocused,
    foregroundInteractive,
    selfScheduling,
    cpuRuntimePlan
  };
}

function formatUptimeSeconds(value: number) {
  const totalSeconds = Math.max(0, Math.floor(value));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}
