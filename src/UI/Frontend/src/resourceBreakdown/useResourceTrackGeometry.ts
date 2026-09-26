import { createSignal, onCleanup } from "solid-js";

export function createResourceTrackGeometry() {
  const [contentWidth, setContentWidth] = createSignal(0);
  const [devicePixelRatio, setDevicePixelRatio] = createSignal(currentDevicePixelRatio());
  const [segmentHeight, setSegmentHeight] = createSignal(0);
  const [segmentTop, setSegmentTop] = createSignal(0);
  let track: HTMLElement | null = null;
  let observer: ResizeObserver | null = null;

  const update = () => {
    if (!track) {
      return;
    }

    const ratio = currentDevicePixelRatio();
    const rect = track.getBoundingClientRect();
    const style = getComputedStyle(track);
    const borderTop = Number.parseFloat(style.borderTopWidth || "0") || 0;
    const borderBottom = Number.parseFloat(style.borderBottomWidth || "0") || 0;
    const contentHeight = Math.max(0, rect.height - borderTop - borderBottom);
    const baseInset = 1;
    const baseSegmentHeight = Math.max(0, contentHeight - baseInset * 2);
    const baseGlobalTop = rect.top + borderTop + baseInset;
    const baseGlobalBottom = baseGlobalTop + baseSegmentHeight;
    const topDevicePx = Math.ceil(baseGlobalTop * ratio - 0.0001);
    const bottomDevicePx = Math.max(topDevicePx + 1, Math.floor(baseGlobalBottom * ratio + 0.0001));
    const alignedTop = topDevicePx / ratio - rect.top - borderTop;
    const alignedHeight = (bottomDevicePx - topDevicePx) / ratio;
    const clampedTop = clampNumber(alignedTop, 0, contentHeight);
    const clampedHeight = Math.max(0, Math.min(alignedHeight, contentHeight - clampedTop));

    setContentWidth(Math.max(0, track.clientWidth));
    setDevicePixelRatio(ratio);
    setSegmentTop(clampedTop);
    setSegmentHeight(clampedHeight);
  };

  const observeTrack = (element: HTMLElement) => {
    track = element;
    update();

    observer?.disconnect();
    observer = new ResizeObserver(update);
    observer.observe(element);
  };

  onCleanup(() => {
    observer?.disconnect();
  });

  return {
    contentWidth,
    devicePixelRatio,
    segmentHeight,
    segmentTop,
    observeTrack
  };
}

function currentDevicePixelRatio() {
  return Math.max(1, window.devicePixelRatio || 1);
}

function clampNumber(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}
