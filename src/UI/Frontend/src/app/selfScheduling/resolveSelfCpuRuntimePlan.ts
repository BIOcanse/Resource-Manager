import {
  normalizeFrontendHiddenRefreshMode,
  resolveLogicRefreshIntervalMs
} from "../../stores/settingsStore";
import type {
  AppLogicRefreshIntervalKey,
  AppOptimizationMode,
  AppPerformanceSettings,
  ResourceManagerSelfCpuGrade
} from "../../types";
import { optimizationModeHasDomain } from "../../features/settings/optimizationMode.ts";

export interface ResourceManagerSelfCpuRuntimePlan {
  frontendRefreshPaused: boolean;
  refreshIntervalMs: Record<AppLogicRefreshIntervalKey, number>;
}

export function resolveSelfCpuRuntimePlan(
  performance: AppPerformanceSettings | undefined,
  optimizationMode: AppOptimizationMode,
  cpuGrade: ResourceManagerSelfCpuGrade,
  frontendVisible: boolean,
  foregroundInteractive: boolean
): ResourceManagerSelfCpuRuntimePlan {
  return {
    frontendRefreshPaused: resolveFrontendRefreshPaused(
      performance,
      optimizationMode,
      cpuGrade,
      frontendVisible),
    refreshIntervalMs: {
      monitor: resolveLogicRefreshIntervalMs("monitor", performance?.monitorRefreshIntervalMs, cpuGrade, foregroundInteractive),
      resourceTable: resolveLogicRefreshIntervalMs("resourceTable", performance?.resourceTableRefreshIntervalMs, cpuGrade, foregroundInteractive),
      management: resolveLogicRefreshIntervalMs("management", performance?.managementRefreshIntervalMs, cpuGrade, foregroundInteractive),
      discovery: resolveLogicRefreshIntervalMs("discovery", performance?.discoveryRefreshIntervalMs, cpuGrade, foregroundInteractive),
      optimization: resolveLogicRefreshIntervalMs("optimization", performance?.optimizationRefreshIntervalMs, cpuGrade, foregroundInteractive),
      localSystem: resolveLogicRefreshIntervalMs("localSystem", performance?.localSystemRefreshIntervalMs, cpuGrade, foregroundInteractive)
    }
  };
}

function resolveFrontendRefreshPaused(
  performance: AppPerformanceSettings | undefined,
  optimizationMode: AppOptimizationMode,
  cpuGrade: ResourceManagerSelfCpuGrade,
  frontendVisible: boolean
) {
  if (frontendVisible) {
    return false;
  }

  const hiddenRefreshMode = normalizeFrontendHiddenRefreshMode(performance?.frontendHiddenRefreshMode);
  if (hiddenRefreshMode === "continueWhenHidden") {
    return false;
  }

  if (hiddenRefreshMode === "pauseWhenHidden") {
    return true;
  }

  return !optimizationModeHasDomain(optimizationMode, "cpu")
    || cpuGrade === "optimize";
}
