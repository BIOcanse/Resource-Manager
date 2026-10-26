import { uiText } from "../../text.ts";

/*
 * 专用显存指标能力在 GPU 调度模型上的三种状态。能力由后端认定（/api/gpu/performance-scores 条目的
 * hasDedicatedMemoryMetrics）：true 有专用显存指标，false 没有，null 后端还不知道。
 * 这里不按有没有显存读数推断；不知道时显存一项按「不知道」显示，也不参与「已满」判断。
 */
export type DedicatedMemoryState = "supported" | "unsupported" | "unknown";

export function dedicatedMemoryState(value: boolean | null): DedicatedMemoryState {
  return value === null ? "unknown" : value ? "supported" : "unsupported";
}

/** 显存条没有百分比时写的字：有专用显存但没读数写「暂无数据」，没有专用显存写「共享」，不知道写「未知」。 */
export function dedicatedMemoryEmptyLabel(value: boolean | null): string {
  return value === null
    ? uiText.gpuScheduling.dedicatedMemoryUnknown
    : value ? uiText.gpuScheduling.noData : uiText.gpuScheduling.shared;
}

export function dedicatedMemoryHint(value: boolean | null): string {
  return uiText.gpuScheduling.dedicatedMemoryHint[dedicatedMemoryState(value)];
}

/**
 * 「已满」判断：GPU 占用到阈值即满；显存占用到阈值算满只在有专用显存时成立。
 * 判为「未满」要求每一项能判断的都有读数和阈值；不知道有没有专用显存时，显存一项无从判断，结果为 null。
 */
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
