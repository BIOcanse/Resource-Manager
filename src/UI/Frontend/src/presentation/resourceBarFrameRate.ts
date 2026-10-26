import type { AppResourceBarFrameRateMode } from "../types.ts";
import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";

// 资源条动画帧率。设置里两项：resourceBarFrameRateMode（display = 跟随屏幕，custom = 自选）
// 和 resourceBarFrameRate（custom 时的帧/秒）。范围和默认值与后端 AppSettingsDefaults 一致。
// App 把生效值写到 body 的 data-resource-bar-frame-rate 上（跟随屏幕写 "display"，自选写帧数），
// 资源条画布读它决定过渡中哪些帧要画。只在资源条有过渡（会动的动画风格）时起作用。
export const resourceBarFrameRateMin = 10;
export const resourceBarFrameRateMax = 240;
export const resourceBarFrameRateDefault = ResourceManager.FrontendPoints.BarFrameRate;

/** 生效的帧率：display = 每个动画帧都画；数字 = 每秒最多画这么多帧。 */
export type ResourceBarFrameRate = "display" | number;

// rAF 时间戳有抖动：差 1 ms 以内也算到点，60 帧在 180 Hz 屏上才是每 3 帧画一次，不会被抖成每 4 帧。
const frameIntervalToleranceMs = 1;

export function normalizeResourceBarFrameRateMode(mode?: string | null): AppResourceBarFrameRateMode {
  return mode === "custom" ? "custom" : ResourceManager.FrontendPoints.BarFrameRateMode;
}

/** 不是整数时用默认值，整数夹到 10–240。 */
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

/** 读 body 上的 data-resource-bar-frame-rate；还没写（App 未就绪、测试页）时按跟随屏幕。 */
export function parseResourceBarFrameRate(value?: string | null): ResourceBarFrameRate {
  return value === undefined || value === null || value === "display"
    ? "display"
    : normalizeResourceBarFrameRate(Number(value));
}

/**
 * 过渡中的这一帧画不画。跟随屏幕时每帧都画；自选帧率时，距上次真正画出的那一帧至少隔 1000/帧率 毫秒。
 * lastDrawnAt 为 null 表示这段过渡还没画过，第一帧照画。过渡的最后一帧不经过这里，总是画。
 */
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
