import { usePushSource } from "../frontendRuntime/push/usePushSource.ts";
import { createMemo, createSignal, onCleanup, onMount } from "solid-js";
import type { Accessor } from "solid-js";
import type { LocalSystemStatus } from "../data/localSystem/localSystemStatusApi";
import type { FrontendWorkController } from "../frontendWork/useFrontendWorkController";
import type { PushValueSource } from "../frontendRuntime/push/PushValueSourceFamily";
import type { FrontendReadIntervals, FrontendRefreshIntervals } from "../types";

interface RuntimeReadContextOptions {
  localSystemSource: PushValueSource<LocalSystemStatus>;
  frontendWork: Pick<FrontendWorkController, "frontendVisible">;
  refreshIntervals: Accessor<FrontendRefreshIntervals | null>;
}

export function useRuntimeReadContext(options: RuntimeReadContextOptions) {
  const [frontendFocused, setFrontendFocused] = createSignal(document.hasFocus());
  const frontendVisible = options.frontendWork.frontendVisible;
  const performanceSettingsAvailable = createMemo(() => options.refreshIntervals() !== null);
  // Resolve intervals from refresh settings; unrelated settings can replace the settings object.
  // Replace subscriptions only when the interval changes.
  const readIntervals = createMemo<FrontendReadIntervals | null>(() => options.refreshIntervals()?.resolved ?? null,
    null, { equals: (left, right) => left?.monitor === right?.monitor
      && left?.resourceTable === right?.resourceTable && left?.localSystem === right?.localSystem });
  const currentLocalSystemRefreshIntervalMs = createMemo(
    () => readIntervals()?.localSystem ?? null);
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
    readIntervals
  };
}

function formatUptimeSeconds(value: number) {
  const totalSeconds = Math.max(0, Math.floor(value));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}
