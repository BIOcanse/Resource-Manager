import type { FrontendReadIntervals } from "../../types.ts";
import type { SourceDemand } from "../../frontendRuntime/source/SourceDescriptor.ts";

/** GET-only sources use the existing local-system refresh preference and registry scheduler. */
export function memoryProtectionDemand(active: boolean, intervals: FrontendReadIntervals): SourceDemand {
  return { active, refreshIntervalMs: intervals.localSystem };
}
