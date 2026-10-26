import { usePushSource } from "../frontendRuntime/push/usePushSource.ts";
import { createMemo, createSignal, onCleanup, onMount } from "solid-js";
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
  const frontendVisible = options.frontendWork.frontendVisible;
  const performanceSettingsAvailable = createMemo(() => options.refreshIntervals() !== null);
  // Settings publications also change for language and other unrelated edits.
  // Only a different cadence may replace this observation's subscription.
  const selfSchedulingIntervalMs = createMemo(
    () => options.refreshIntervals()?.foreground.monitor ?? null);
  const schedulingSource = usePushSource(options.selfSchedulingSource, () => ({
    active: frontendVisible(), intervalMs: selfSchedulingIntervalMs()
  }));
  const selfScheduling = () => schedulingSource.snapshot().data;
  const foregroundInteractive = createMemo(() => frontendVisible() && frontendFocused());
  const cpuRuntimePlan = createMemo(() => resolveSelfCpuRuntimePlan(
    options.refreshIntervals(),
    selfScheduling()?.cpuGrade ?? "normal",
    foregroundInteractive()), null, { equals: (left, right) => left?.monitor === right?.monitor
      && left?.resourceTable === right?.resourceTable && left?.localSystem === right?.localSystem });
  const currentLocalSystemRefreshIntervalMs = createMemo(
    () => cpuRuntimePlan()?.localSystem ?? null);
  const localSource = usePushSource(options.localSystemSource, () => ({
    active: performanceSettingsAvailable() && frontendVisible(),
    intervalMs: currentLocalSystemRefreshIntervalMs()
  }));
  const localSystemStatus = () => localSource.snapshot().data;
  const systemUptime = createMemo(() => {
    const status = localSystemStatus();
    return status ? formatUptimeSeconds(status.uptimeSeconds) : "--";
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
