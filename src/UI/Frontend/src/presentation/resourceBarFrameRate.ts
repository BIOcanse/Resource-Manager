import type { AppResourceBarFrameRateMode } from "../types.ts";
import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";

// Resource bars use resourceBarFrameRateMode (display/custom) and custom frames per second.
// Ranges and defaults match backend AppSettingsDefaults.
// App publishes display or the numeric rate in body[data-resource-bar-frame-rate].
// Canvas transitions read this cap; it matters only while bars animate.
export const resourceBarFrameRateMin = 10;
export const resourceBarFrameRateMax = 240;
export const resourceBarFrameRateDefault = ResourceManager.FrontendPoints.BarFrameRate;

/** display draws every animation frame; a number caps frames per second. */
export type ResourceBarFrameRate = "display" | number;

// Allow 1 ms timestamp jitter so 60 fps on a 180 Hz screen draws every third frame.
const frameIntervalToleranceMs = 1;

export function normalizeResourceBarFrameRateMode(mode?: string | null): AppResourceBarFrameRateMode {
  return mode === "custom" ? "custom" : ResourceManager.FrontendPoints.BarFrameRateMode;
}

/** Non-integers use the default; integers clamp to 10-240. */
export function normalizeResourceBarFrameRate(framesPerSecond?: number | null): number {
  if (typeof framesPerSecond !== "number" || !Number.isInteger(framesPerSecond)) {
    return resourceBarFrameRateDefault;
  }

  return Math.min(resourceBarFrameRateMax, Math.max(resourceBarFrameRateMin, framesPerSecond));
}

export function resolveResourceBarFrameRate(appearance?: {
  resourceBarFrameRateMode?: string | null;
  resourceBarFrameRate?: number | null;
} | null): ResourceBarFrameRate {
  return normalizeResourceBarFrameRateMode(appearance?.resourceBarFrameRateMode) === "custom"
    ? normalizeResourceBarFrameRate(appearance?.resourceBarFrameRate)
    : "display";
}

/** Read the body frame-rate attribute; absent attributes follow the display. */
export function parseResourceBarFrameRate(value?: string | null): ResourceBarFrameRate {
  return value === undefined || value === null || value === "display"
    ? "display"
    : normalizeResourceBarFrameRate(Number(value));
}

/** Draw the first frame; custom mode then requires 1000/rate ms since the last draw. Display mode draws every frame. The final transition frame bypasses this check. */
export function shouldDrawResourceBarFrame(
  frameRate: ResourceBarFrameRate,
  lastDrawnAt: number | null,
  now: number
): boolean {
  if (frameRate === "display" || lastDrawnAt === null) {
    return true;
  }

  return now - lastDrawnAt >= 1000 / frameRate - frameIntervalToleranceMs;
}
