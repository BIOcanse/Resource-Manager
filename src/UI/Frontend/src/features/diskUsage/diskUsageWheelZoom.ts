import { zoomAt, type DiskUsageViewport } from "./diskUsageViewport.ts";

/** Accumulate wheel input into a zoom target and restart easing from the current viewport so repeated input stays continuous. */
export interface DiskUsageWheelZoom {
  /** Viewport visible when this transition began. */
  from: DiskUsageViewport;
  /** Accumulated zoom target, constrained by zoomAt. */
  to: DiskUsageViewport;
  startedAt: number;
  durationMs: number;
}

/** Zoom multiplier per wheel notch; negative deltaY zooms in. */
const factorPerNotch = 1.2;

/** Accumulate signed notches on the pending target while starting from the currently visible viewport. */
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

/** Interpolate visible span and translation with eased progress to preserve the pointer anchor; zero duration returns the target. */
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
