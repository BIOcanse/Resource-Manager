import { readFileSync } from "node:fs";
import type { AppPerformanceSettings, FrontendRefreshIntervals } from "../../src/types.ts";

// Produced by AppFrontendRefreshIntervalsTests from the production settings DTO.
export const frontendRefreshIntervals = JSON.parse(readFileSync(
  new URL("./frontendRefreshIntervals.json", import.meta.url), "utf8")) as FrontendRefreshIntervals;

// Mock settings commits keep the producer fixture's catalogue and project each
// explicit choice. This helper belongs to the HTTP fixture, never product code.
export function mockFrontendRefreshIntervals(performance?: Partial<AppPerformanceSettings>): FrontendRefreshIntervals {
  const publication = structuredClone(frontendRefreshIntervals);
  for (const key of ["monitor", "resourceTable", "localSystem"] as const) {
    const setting = performance?.[`${key}RefreshIntervalMs`];
    if (setting?.mode === "custom" || setting?.mode === "preset") {
      const value = setting.mode === "custom" ? setting.customValue : publication.presets[key][setting.preset];
      (publication.foreground as Record<string, number>)[key] = value;
      (publication.backgroundOptimized as Record<string, number>)[key] = value;
    }
  }
  return publication;
}
