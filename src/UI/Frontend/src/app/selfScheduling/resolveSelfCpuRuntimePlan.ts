import type { FrontendReadIntervals, FrontendRefreshIntervals, ResourceManagerSelfCpuGrade } from "../../types.ts";

// The backend expands both contexts from the committed settings. Selecting a
// context here preserves local foreground ownership without repeating presets.
export function resolveSelfCpuRuntimePlan(
  publication: FrontendRefreshIntervals | null,
  cpuGrade: ResourceManagerSelfCpuGrade,
  foregroundInteractive: boolean
): FrontendReadIntervals | null {
  if (!publication) return null;
  return cpuGrade === "optimize" && !foregroundInteractive
    ? publication.backgroundOptimized
    : publication.foreground;
}
