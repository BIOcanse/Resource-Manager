import { observeLayout } from "../../interactions/solid/layoutObservation.ts";
import { batch, createSignal, onCleanup } from "solid-js";

export function createResourceTrackGeometry() {
  const [contentWidth, setContentWidth] = createSignal(0);
  const [devicePixelRatio, setDevicePixelRatio] = createSignal(currentDevicePixelRatio());
  const [segmentHeight, setSegmentHeight] = createSignal(0);
  const [segmentTop, setSegmentTop] = createSignal(0);
  let track: HTMLElement | null = null;
  let measured: { width: number; ratio: number; top: number; height: number } | null = null;

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

    measured = { width: Math.max(0, track.clientWidth), ratio, top: clampedTop, height: clampedHeight };
  };

  const layout = observeLayout({
    read: update,
    write: () => {
      if (!measured) return;
      const geometry = measured;
      batch(() => {
        setContentWidth(geometry.width);
        setDevicePixelRatio(geometry.ratio);
        setSegmentTop(geometry.top);
        setSegmentHeight(geometry.height);
      });
    }
  });
  const observeTrack = (element: HTMLElement) => {
    track = element;
    layout.update([element]);
  };
  onCleanup(() => layout.dispose());

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
