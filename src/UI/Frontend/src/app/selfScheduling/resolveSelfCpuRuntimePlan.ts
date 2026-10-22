import { resolveLogicRefreshIntervalMs } from "../../stores/settingsStore";
import type {
  AppLogicRefreshIntervalKey,
  AppPerformanceSettings,
  ResourceManagerSelfCpuGrade
} from "../../types";

// 窗口隐藏时一律暂停，不再由设置决定；各数据源各自按 frontendVisible() 停止订阅。
export interface ResourceManagerSelfCpuRuntimePlan {
  refreshIntervalMs: Record<AppLogicRefreshIntervalKey, number>;
}

export function resolveSelfCpuRuntimePlan(
  performance: AppPerformanceSettings | undefined,
  cpuGrade: ResourceManagerSelfCpuGrade,
  foregroundInteractive: boolean
): ResourceManagerSelfCpuRuntimePlan {
  return {
    refreshIntervalMs: {
      monitor: resolveLogicRefreshIntervalMs("monitor", performance?.monitorRefreshIntervalMs, cpuGrade, foregroundInteractive),
      resourceTable: resolveLogicRefreshIntervalMs("resourceTable", performance?.resourceTableRefreshIntervalMs, cpuGrade, foregroundInteractive),
      localSystem: resolveLogicRefreshIntervalMs("localSystem", performance?.localSystemRefreshIntervalMs, cpuGrade, foregroundInteractive)
    }
  };
}
