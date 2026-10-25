import { zoomAt, type DiskUsageViewport } from "./diskUsageViewport.ts";

/**
 * 方格图的滚轮缩放。
 *
 * 每一格滚轮把缩放目标再乘（或除）1.2，触控板的零碎滚动按格数取零点几次方，所以缩放量跟着滚动量走。
 * 画面不直接跳到目标，而是从眼下看到的视口用一段过渡带过去（时长、曲线由调用方从动效令牌读）。
 * 过渡还没走完又滚了一格：目标在上一个目标上接着累积，过渡从眼下看到的位置重新出发，画面不跳回、不重头开始。
 */
export interface DiskUsageWheelZoom {
  /** 这一段过渡的起点：上一格滚到时眼下看到的视口。 */
  from: DiskUsageViewport;
  /** 累积到现在的缩放目标。缩放上下限由 zoomAt 管。 */
  to: DiskUsageViewport;
  startedAt: number;
  durationMs: number;
}

/** 每格滚轮的倍数。向上（deltaY < 0）放大。 */
const factorPerNotch = 1.2;

/**
 * 又滚了 notches 格（wheelNotches 换算过的，带正负）：目标在正在走的过渡的目标上累积，
 * 没有过渡就在眼下的视口上累积；新的一段从眼下看到的视口出发。
 */
export function accumulateWheelZoom(
  running: DiskUsageWheelZoom | null,
  shown: DiskUsageViewport,
  canvasWidth: number,
  canvasHeight: number,
  pointerX: number,
  pointerY: number,
  notches: number,
  now: number,
  durationMs: number
): DiskUsageWheelZoom {
  const base = running ? running.to : shown;
  const to = zoomAt(base, canvasWidth, canvasHeight, pointerX, pointerY, factorPerNotch ** -notches);
  return { from: shown, to, startedAt: now, durationMs };
}

/**
 * 过渡在 now 这一刻的视口，ease 是进度 → 缓动后的进度。时长 0 直接是目标。
 * 可见宽度（1 / 倍数）和平移量都按缓动后的进度线性插：平移量对可见宽度是线性的，
 * 所以一路上指针下的那一点始终停在指针下，和一次到位的 zoomAt 同一个锚。
 */
export function wheelZoomAt(
  zoom: DiskUsageWheelZoom,
  now: number,
  ease: (progress: number) => number
): { viewport: DiskUsageViewport; done: boolean } {
  const progress = zoom.durationMs <= 0 ? 1 : Math.min(1, Math.max(0, (now - zoom.startedAt) / zoom.durationMs));
  if (progress >= 1) {
    return { viewport: zoom.to, done: true };
  }
  const k = ease(progress);
  const span = 1 / zoom.from.scale + (1 / zoom.to.scale - 1 / zoom.from.scale) * k;
  return {
    viewport: {
      scale: 1 / span,
      offsetX: zoom.from.offsetX + (zoom.to.offsetX - zoom.from.offsetX) * k,
      offsetY: zoom.from.offsetY + (zoom.to.offsetY - zoom.from.offsetY) * k
    },
    done: false
  };
}
