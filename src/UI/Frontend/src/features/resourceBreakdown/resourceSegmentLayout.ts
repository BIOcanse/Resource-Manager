import type { JSX } from "solid-js";

export type ResourceSegmentCssVars = JSX.CSSProperties & Record<`--${string}`, string>;

export interface ResourceSegmentPixelGeometry {
  contentWidth: number;
  devicePixelRatio: number;
  segmentHeight: number;
  segmentTop: number;
}

/**
 * 一段在指针和高亮框上至少占多宽（CSS px）。很窄的段照实际比例画，但悬停、选中框和指针命中都按这个宽度，
 * 否则不到 1 px 的段既看不见框也指不到（键盘仍按顺序逐段走）。
 */
export const resourceSegmentMinimumTargetPx = 6;

export interface ResourceSegmentLayout<T extends { value: number }> {
  segment: T;
  index: number;
  width: number;
  left: number;
  right: number;
}

export function resourceSegmentLayout<T extends { value: number }>(
  segments: T[],
  denominator: number,
  options: { fill?: boolean } = {})
: ResourceSegmentLayout<T>[] {
  const positiveSegments = segments.filter((segment) => Number(segment.value) > 0);
  if (positiveSegments.length === 0) {
    return [];
  }

  const rawTotal = positiveSegments.reduce((sum, segment) => sum + Number(segment.value), 0);
  const layoutDenominator = options.fill ? rawTotal : denominator;
  if (layoutDenominator <= 0) {
    return [];
  }

  let left = 0;
  return positiveSegments.map((segment, index) => {
    const isLast = index === positiveSegments.length - 1;
    const width = isLast && options.fill
      ? Math.max(0, 100 - left)
      : Math.max(0, Math.min(100 - left, Number(segment.value) * 100 / layoutDenominator));
    const right = left + width;
    const item = {
      segment,
      index,
      width,
      left,
      right
    };
    left = right;
    return item;
  });
}

export function shouldAnimateResourceSegment(width: number) {
  return Number.isFinite(width) && width > 0;
}

export function resourceSegmentLayerStyle<T extends { value: number }>(
  item: ResourceSegmentLayout<T>,
  geometry?: ResourceSegmentPixelGeometry)
: ResourceSegmentCssVars {
  const width = clampNumber(item.width, 0, 100);
  const pixelVars = resourceSegmentPixelVars(item, geometry);
  return {
    ...pixelVars,
    "--segment-left": `${item.left}%`,
    "--segment-width": `${width}%`
  };
}

function resourceSegmentPixelVars<T extends { value: number }>(
  item: ResourceSegmentLayout<T>,
  geometry?: ResourceSegmentPixelGeometry)
: ResourceSegmentCssVars {
  const vars: ResourceSegmentCssVars = {};
  const segmentTop = Number(geometry?.segmentTop ?? 0);
  if (Number.isFinite(segmentTop) && segmentTop >= 0) {
    vars["--segment-render-top"] = `${segmentTop}px`;
  }

  const segmentHeight = Number(geometry?.segmentHeight ?? 0);
  if (Number.isFinite(segmentHeight) && segmentHeight > 0) {
    vars["--segment-render-height"] = `${segmentHeight}px`;
  }

  const contentWidth = Number(geometry?.contentWidth ?? 0);
  if (!Number.isFinite(contentWidth) || contentWidth <= 0) {
    return vars;
  }

  const devicePixelRatio = Math.max(1, Number(geometry?.devicePixelRatio ?? 1) || 1);
  let leftPx = alignToDevicePixel(item.left * contentWidth / 100, devicePixelRatio);
  let rightPx = alignToDevicePixel(item.right * contentWidth / 100, devicePixelRatio);
  const minimumPx = Math.min(resourceSegmentMinimumTargetPx, contentWidth);
  if (rightPx - leftPx < minimumPx) {
    // 窄段的高亮框以它为中心放宽到最小宽度，不出轨道。
    const centre = (item.left + item.right) * contentWidth / 200;
    leftPx = alignToDevicePixel(clampNumber(centre - minimumPx / 2, 0, contentWidth - minimumPx), devicePixelRatio);
    rightPx = leftPx + minimumPx;
  }
  const widthPx = Math.max(0, rightPx - leftPx);
  vars["--segment-render-left"] = "0px";
  vars["--segment-render-width"] = `${widthPx}px`;
  vars["--segment-render-x"] = `${leftPx}px`;
  return vars;
}

function alignToDevicePixel(value: number, devicePixelRatio: number) {
  return Math.floor(value * devicePixelRatio) / devicePixelRatio;
}

function clampNumber(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}
