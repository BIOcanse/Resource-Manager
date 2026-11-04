import { uiText } from "../../text.ts";

/* Dedicated-memory capability comes from the backend: true, false, or unknown. Missing readings do not determine capability; unknown memory cannot establish a non-full result. */
export type DedicatedMemoryState = "supported" | "unsupported" | "unknown";

export function dedicatedMemoryState(value: boolean | null): DedicatedMemoryState {
  return value === null ? "unknown" : value ? "supported" : "unsupported";
}

/** Without a percentage, distinguish missing readings, shared memory, and unknown capability. */
export function dedicatedMemoryEmptyLabel(value: boolean | null): string {
  return value === null
    ? uiText.gpuScheduling.dedicatedMemoryUnknown
    : value ? uiText.gpuScheduling.noData : uiText.gpuScheduling.shared;
}

export function dedicatedMemoryHint(value: boolean | null): string {
  return uiText.gpuScheduling.dedicatedMemoryHint[dedicatedMemoryState(value)];
}

/** GPU utilization can establish fullness; memory can only do so with dedicated-memory capability. A non-full result requires all applicable readings and thresholds; otherwise return null. */
export function gpuFullClassification(
  usagePercent: number | null,
  vramPercent: number | null,
  gpuUsageFullPressurePercent: number | null,
  gpuVramFullPressurePercent: number | null,
  hasDedicatedMemoryMetrics: boolean | null): {
    full: boolean | null;
    reason: string | null;
  } {
  const reasons = [
    gpuUsageFullPressurePercent !== null
        && usagePercent !== null
        && usagePercent >= gpuUsageFullPressurePercent
      ? uiText.gpuScheduling.gpuFull
      : "",
    hasDedicatedMemoryMetrics === true
        && gpuVramFullPressurePercent !== null
        && vramPercent !== null
        && vramPercent >= gpuVramFullPressurePercent
      ? uiText.gpuScheduling.vramFull
      : ""
  ].filter(Boolean);
  if (reasons.length > 0) {
    return { full: true, reason: reasons.join(" / ") };
  }

  const canClassify = gpuUsageFullPressurePercent !== null
    && usagePercent !== null
    && (hasDedicatedMemoryMetrics === false
      || (hasDedicatedMemoryMetrics === true
        && gpuVramFullPressurePercent !== null
        && vramPercent !== null));
  return canClassify
    ? { full: false, reason: null }
    : { full: null, reason: null };
}
