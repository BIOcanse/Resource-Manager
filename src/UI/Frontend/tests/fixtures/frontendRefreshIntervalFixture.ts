import { pointPreferences, resolveFrontendRefresh } from "../../src/preferences/frontendPreferences.ts";
import type { AppPerformanceSettings, FrontendRefreshIntervals } from "../../src/types.ts";

// The frontend projection is the sole default source. No backend cadence DTO exists.
export const frontendRefreshIntervals = resolveFrontendRefresh(pointPreferences().refresh);

// Mock settings commits keep the producer fixture's catalogue and project each
// explicit choice. This helper belongs to the HTTP fixture, never product code.
export function mockFrontendRefreshIntervals(performance?: Partial<AppPerformanceSettings>): FrontendRefreshIntervals {
  const publication = structuredClone(frontendRefreshIntervals);
  for (const key of ["monitor", "resourceTable", "localSystem"] as const) {
    const setting = performance?.[`${key}RefreshIntervalMs`];
    if (setting?.mode === "custom" || setting?.mode === "preset") {
      const value = setting.mode === "custom" ? setting.customValue : publication.presets[key][setting.preset];
      (publication.resolved as Record<string, number>)[key] = value;
    }
  }
  return publication;
}
